//! CEF `App`, `Client`, `BrowserProcessHandler`, `RenderHandler`,
//! and `DisplayHandler` wrappers for the Dragonglass HUD sidecar.
//!
//! The layering:
//!
//! * `KspApp` — what we pass to `cef::initialize`. Supplies a
//!   `BrowserProcessHandler` and tweaks Chromium command-line switches.
//! * `KspBrowserProcessHandler` — once CEF is initialized, creates a
//!   single browser in off-screen rendering mode pointed at our HUD
//!   URL. The browser lives inside CEF; we don't hold a reference.
//! * `KspClient` — per-browser callback bundle; returns the render
//!   and display handlers to CEF.
//! * `KspRenderHandler` — `view_rect` reports our framebuffer
//!   dimensions; `on_accelerated_paint` receives per-frame GPU-shared
//!   texture handles from CEF's compositor (IOSurfaceRef on macOS,
//!   DXGI shared NT HANDLE on Windows), blits them into a persistent
//!   canvas texture, and publishes the canvas handle via `ShmWriter`.
//! * `KspDisplayHandler` — forwards JS `console.*` output to stdout
//!   so the C# `SidecarHost` can route it to `UDebug.Log`.

use std::sync::atomic::{AtomicU32, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};

use cef::*;

use dg_shm::ShmWriter;

/// Shared slot holding the single Browser instance.
/// `on_context_initialized` populates it once CEF is ready; the main
/// pump loop reads from it to dispatch mouse events through
/// `BrowserHost::send_mouse_*`.
pub type BrowserSlot = Arc<Mutex<Option<Browser>>>;

/// Bootstrap viewport size. CEF renders into an IOSurface on the GPU
/// and the plugin wraps that IOSurface as an MTLTexture via
/// `Texture2D.CreateExternalTexture` — the BGRA bytes never flow
/// through shared memory.
///
/// These constants seed the very first `ShmWriter::create` and
/// `IOSurfaceBridge::create` calls so the sidecar has something
/// paintable before the plugin connects. The plugin then pushes its
/// real `Screen.width/height` over the SHM input ring (as
/// `INPUT_RESIZE`), the sidecar recreates the bridge at the new dims,
/// and everything converges. Any sensible resolution works here; 1920×1200
/// just happens to match the developer's machine.
pub const INITIAL_WIDTH: u32 = 1920;
pub const INITIAL_HEIGHT: u32 = 1200;

// -----------------------------------------------------------------------
// RenderHandler: the actual OnPaint sink
// -----------------------------------------------------------------------

/// Inner render handler state. Clone-able because the `wrap_render_handler!`
/// macro copies it internally. All mutable state lives behind `Arc<…>` —
/// width/height in atomics so `view_rect` is lock-free, the canvas
/// bridge in a Mutex so resize can atomically swap it between frames.
#[derive(Clone)]
pub struct KspRenderHandlerInner {
    /// Current viewport width in **physical pixels** — what the
    /// plugin sends via INPUT_RESIZE and what CEF ultimately rasters
    /// into. `view_rect` divides this by the device scale factor
    /// before reporting to CEF, so the page sees a DIP-sized canvas
    /// and CEF supersamples up to the physical size.
    width: Arc<AtomicU32>,
    /// Current viewport height in **physical pixels**.
    height: Arc<AtomicU32>,
    /// Device scale factor CEF reports via `screen_info`, stored as
    /// `AtomicU32` bits. Driving `window.devicePixelRatio` in the
    /// page: 1.0 → 1x, 2.0 → Retina/hi-DPI Windows. Set once at
    /// sidecar launch from the `--device-scale` CLI flag.
    device_scale_bits: Arc<AtomicU32>,
    writer: Arc<Mutex<ShmWriter>>,
    frame_counter: Arc<AtomicU64>,
    /// Most recently observed IOSurfaceID from `on_accelerated_paint`.
    /// Used only to detect "CEF rotated to a different surface" so we
    /// can bump `io_surface_gen`.
    last_io_surface_id: Arc<AtomicU32>,
    /// Bumped once per distinct surface rotation. The plugin uses this
    /// to decide when to call `PollSurface` / rebind its external
    /// texture — rebinds only happen when gen changes, not per frame.
    io_surface_gen: Arc<AtomicU32>,
    /// Canvas bridge that receives per-frame CEF IOSurfaces and
    /// composites them into a single globally-lookupable canvas
    /// IOSurface. Swapped on resize — the old bridge is dropped once
    /// no more paint callbacks reference it.
    #[cfg(target_os = "macos")]
    io_surface_bridge: Arc<Mutex<Option<dg_gpu::iosurface_bridge::IOSurfaceBridge>>>,
    /// Canvas bridge that receives per-frame CEF D3D11 shared textures
    /// (DXGI SHARED_NTHANDLE + SHARED_KEYEDMUTEX) and blits them into a
    /// persistent canvas texture with its own shared NT handle.
    /// Swapped on resize. Windows counterpart of `io_surface_bridge`.
    #[cfg(target_os = "windows")]
    d3d11_bridge: Arc<Mutex<Option<dg_gpu::d3d11_bridge::D3D11Bridge>>>,
}

impl KspRenderHandlerInner {
    pub fn new(writer: ShmWriter, device_scale: f32) -> Self {
        let width = writer.width();
        let height = writer.height();
        Self {
            width: Arc::new(AtomicU32::new(width)),
            height: Arc::new(AtomicU32::new(height)),
            device_scale_bits: Arc::new(AtomicU32::new(device_scale.to_bits())),
            writer: Arc::new(Mutex::new(writer)),
            frame_counter: Arc::new(AtomicU64::new(0)),
            last_io_surface_id: Arc::new(AtomicU32::new(0)),
            io_surface_gen: Arc::new(AtomicU32::new(0)),
            #[cfg(target_os = "macos")]
            io_surface_bridge: Arc::new(Mutex::new(None)),
            #[cfg(target_os = "windows")]
            d3d11_bridge: Arc::new(Mutex::new(None)),
        }
    }

    /// Current device scale factor. Read by `view_rect` (to divide
    /// physical dims into DIP), `screen_info` (to report to CEF),
    /// and `main.rs` input-dispatch to scale physical-pixel mouse
    /// coords from the plugin into DIP for CEF.
    pub fn device_scale(&self) -> f32 {
        f32::from_bits(self.device_scale_bits.load(Ordering::Acquire))
    }

    /// Attach (or replace) the mach-port bridge that ships IOSurface
    /// handles to the plugin side. Called once at startup with the
    /// initial bridge, and again from the main loop on every resize
    /// with a fresh bridge at the new dimensions.
    #[cfg(target_os = "macos")]
    pub fn set_io_surface_bridge(&self, bridge: dg_gpu::iosurface_bridge::IOSurfaceBridge) {
        if let Ok(mut slot) = self.io_surface_bridge.lock() {
            *slot = Some(bridge);
        }
    }

    /// Attach (or replace) the D3D11 shared-texture bridge. Windows
    /// counterpart of `set_io_surface_bridge`.
    #[cfg(target_os = "windows")]
    pub fn set_d3d11_bridge(&self, bridge: dg_gpu::d3d11_bridge::D3D11Bridge) {
        if let Ok(mut slot) = self.d3d11_bridge.lock() {
            *slot = Some(bridge);
        }
    }

    /// Update the current viewport dims. The main loop calls this from
    /// the resize handler; `view_rect` picks up the new value on its
    /// next CEF-initiated query.
    pub fn set_size(&self, width: u32, height: u32) {
        self.width.store(width, Ordering::Release);
        self.height.store(height, Ordering::Release);
    }

    /// Accessor so the main loop can lock the writer (for
    /// `set_dimensions` under the seqlock) without reaching into the
    /// field directly.
    pub fn writer(&self) -> &Arc<Mutex<ShmWriter>> {
        &self.writer
    }
}

wrap_render_handler! {
    pub struct KspRenderHandlerBuilder {
        handler: KspRenderHandlerInner,
    }

    impl RenderHandler {
        /// Tell CEF what size to render at, in **DIP** (device-independent
        /// pixels). CEF multiplies by `screen_info.device_scale_factor`
        /// to get the physical paint size — so reporting `physical / scale`
        /// here yields a paint that's exactly `physical` px, with
        /// `window.devicePixelRatio = scale` exposed to the page.
        ///
        /// Called whenever CEF needs the viewport size — once per
        /// browser at creation, and again after every
        /// `BrowserHost::was_resized()` triggered by the main loop's
        /// INPUT_RESIZE handler.
        fn view_rect(&self, _browser: Option<&mut Browser>, rect: Option<&mut Rect>) {
            if let Some(rect) = rect {
                let scale = self.handler.device_scale().max(0.1);
                let w = self.handler.width.load(Ordering::Acquire) as f32;
                let h = self.handler.height.load(Ordering::Acquire) as f32;
                rect.x = 0;
                rect.y = 0;
                rect.width = (w / scale).round() as _;
                rect.height = (h / scale).round() as _;
            }
        }

        /// Focus-tracking hook. CEF calls this whenever a focusable
        /// editable element gains or loses focus in the page —
        /// `input_mode != NONE` means "an editable just took focus"
        /// (what CEF calls "virtual keyboard requested" for the OSR /
        /// mobile path); `input_mode == NONE` means "focus left the
        /// editable". We reflect that into a SHM flag the plugin
        /// polls each frame so it can apply / drop a KSP
        /// `ControlTypes.KEYBOARDINPUT` `InputLockManager` lock —
        /// otherwise KSP shortcut keys fire while the user is typing
        /// into a web input.
        fn on_virtual_keyboard_requested(
            &self,
            _browser: Option<&mut Browser>,
            input_mode: TextInputMode,
        ) {
            let wants = input_mode != TextInputMode::NONE;
            if let Ok(mut writer) = self.handler.writer.lock() {
                writer.write_cef_wants_keyboard(wants);
            }
        }

        /// Report the device scale factor so CEF exposes it to the
        /// page as `window.devicePixelRatio`. Returning 1 tells CEF
        /// we supplied valid data; 0 would make it fall back to its
        /// own default (1.0), defeating hi-DPI.
        fn screen_info(
            &self,
            _browser: Option<&mut Browser>,
            screen_info: Option<&mut ScreenInfo>,
        ) -> ::std::os::raw::c_int {
            let Some(info) = screen_info else { return 0 };
            let scale = self.handler.device_scale().max(0.1);
            info.device_scale_factor = scale;
            let w = self.handler.width.load(Ordering::Acquire) as f32;
            let h = self.handler.height.load(Ordering::Acquire) as f32;
            let rect = Rect {
                x: 0,
                y: 0,
                width: (w / scale).round() as _,
                height: (h / scale).round() as _,
            };
            info.rect = rect.clone();
            info.available_rect = rect;
            1
        }

        /// Zero-copy accelerated-paint path. CEF renders to an IOSurface
        /// on the GPU and hands us a borrow of the `AcceleratedPaintInfo`
        /// descriptor. We never touch pixel bytes — we extract the
        /// `IOSurfaceID` (a stable 32-bit handle into `IOSurfaceLookup`)
        /// and publish it via `ShmWriter::write_header_only`. The plugin
        /// side reads the ID, wraps the surface as an `MTLTexture` via
        /// its native rendering dylib, and hands it to
        /// `Texture2D.CreateExternalTexture` — no memcpy, no upload.
        ///
        /// Enabled by `shared_texture_enabled=1` on the `WindowInfo`
        /// used at browser creation.
        #[cfg(target_os = "macos")]
        fn on_accelerated_paint(
            &self,
            _browser: Option<&mut Browser>,
            type_: PaintElementType,
            _dirty_rects: Option<&[Rect]>,
            info: Option<&AcceleratedPaintInfo>,
        ) {
            use std::sync::atomic::Ordering;
            let Some(info) = info else { return };
            if type_ != PaintElementType::default() {
                return;
            }
            let handle = info.shared_texture_io_surface;
            if handle.is_null() {
                return;
            }

            // SAFETY: CEF guarantees `handle` is a valid IOSurfaceRef
            // for the duration of this callback. We only need the ID
            // for cache keying on the blitter side.
            let cef_id = unsafe {
                let surface: &objc2_io_surface::IOSurfaceRef =
                    &*handle.cast::<objc2_io_surface::IOSurfaceRef>();
                surface.id()
            };

            // Blit CEF's per-frame IOSurface into our persistent
            // canvas IOSurface. The canvas has `kIOSurfaceIsGlobal`
            // set so the plugin can look it up by ID. This is the
            // whole zero-copy pipeline: one GPU-local blit on our
            // side replaces the mach-port transport we'd otherwise
            // need (CEF's own IOSurfaces aren't globally visible).
            let canvas_id = match self.handler.io_surface_bridge.lock() {
                Ok(guard) => match guard.as_ref() {
                    Some(bridge) => {
                        bridge.blit_from_cef(handle as *const _, cef_id);
                        bridge.canvas_id()
                    }
                    None => 0,
                },
                Err(e) => {
                    eprintln!("bridge mutex poisoned: {e}");
                    0
                }
            };

            // Gen counter advances every frame so the plugin knows
            // there's fresh canvas content to re-blit into its Unity
            // destination texture. Since the canvas id never changes,
            // the plugin caches its GL rect-texture wrapper once.
            self.handler.last_io_surface_id.store(canvas_id, Ordering::Relaxed);
            let gen = self.handler.io_surface_gen.fetch_add(1, Ordering::Relaxed) + 1;

            match self.handler.writer.lock() {
                Ok(mut writer) => {
                    writer.write_header_only(canvas_id, gen);
                    self.handler.frame_counter.fetch_add(1, Ordering::Relaxed);
                }
                Err(e) => {
                    eprintln!("writer mutex poisoned: {e}");
                }
            }
        }

        /// Windows accelerated-paint path. CEF hands us a HANDLE to a
        /// D3D11 shared NT texture containing the latest frame. We
        /// open it, bind it as a shader-resource view, and render a
        /// fullscreen triangle into our canvas RTV that un-premultiplies
        /// alpha as it writes. CEF's cefclient sample takes the same
        /// approach — SRV sample + draw, NOT `CopyResource` — which
        /// is the only read path that surfaces actual pixels from
        /// Chromium's NT-handle shared texture on Windows in CEF 146.
        #[cfg(target_os = "windows")]
        fn on_accelerated_paint(
            &self,
            _browser: Option<&mut Browser>,
            type_: PaintElementType,
            _dirty_rects: Option<&[Rect]>,
            info: Option<&AcceleratedPaintInfo>,
        ) {
            use std::sync::atomic::Ordering;
            let Some(info) = info else { return };
            if type_ != PaintElementType::default() {
                return;
            }
            let cef_handle = info.shared_texture_handle;
            if cef_handle.is_null() {
                return;
            }

            let canvas_id = match self.handler.d3d11_bridge.lock() {
                Ok(guard) => match guard.as_ref() {
                    Some(bridge) => {
                        if let Err(e) = bridge.blit_from_cef(cef_handle) {
                            eprintln!("d3d11 blit failed: {e}");
                        }
                        bridge.canvas_id()
                    }
                    None => 0,
                },
                Err(e) => {
                    eprintln!("d3d11 bridge mutex poisoned: {e}");
                    0
                }
            };

            self.handler
                .last_io_surface_id
                .store(canvas_id, Ordering::Relaxed);
            let gen = self
                .handler
                .io_surface_gen
                .fetch_add(1, Ordering::Relaxed)
                + 1;

            match self.handler.writer.lock() {
                Ok(mut writer) => {
                    writer.write_header_only(canvas_id, gen);
                    self.handler.frame_counter.fetch_add(1, Ordering::Relaxed);
                }
                Err(e) => {
                    eprintln!("writer mutex poisoned: {e}");
                }
            }
        }

    }
}

impl KspRenderHandlerBuilder {
    pub fn build(handler: KspRenderHandlerInner) -> RenderHandler {
        Self::new(handler)
    }
}

// -----------------------------------------------------------------------
// DisplayHandler: forwards JS console output to stdout
// -----------------------------------------------------------------------

wrap_display_handler! {
    pub struct KspDisplayHandlerBuilder;

    impl DisplayHandler {
        fn on_console_message(
            &self,
            _browser: Option<&mut Browser>,
            level: LogSeverity,
            message: Option<&CefString>,
            source: Option<&CefString>,
            line: ::std::os::raw::c_int,
        ) -> ::std::os::raw::c_int {
            let tag = if level == LogSeverity::WARNING {
                "WARN"
            } else if level == LogSeverity::ERROR {
                "ERR"
            } else {
                "LOG"
            };
            let msg = message
                .map(|s| s.to_string())
                .unwrap_or_default();
            let src = source
                .map(|s| s.to_string())
                .unwrap_or_default();
            // stdout so C# SidecarHost routes it to UDebug.Log
            // with [Dragonglass/HUD/JS] prefix → KSP.log + debug console.
            println!("[{tag}] {msg} ({src}:{line})");
            1 // returning 1 suppresses CEF's own console logging
        }
    }
}

// -----------------------------------------------------------------------
// RenderProcessHandler: installs the punch-rects V8 binding on every
// new V8 context. Created in every process the App is passed to; CEF
// only invokes these methods in renderer subprocesses (the helper
// binary sets things up via execute_process).
// -----------------------------------------------------------------------

wrap_render_process_handler! {
    pub struct KspRenderProcessHandlerBuilder {}

    impl RenderProcessHandler {
        /// Install `window.dgUpdatePunchRects` so the page's rAF pump
        /// can ship the stream-rect snapshot to the browser process.
        fn on_context_created(
            &self,
            _browser: Option<&mut Browser>,
            _frame: Option<&mut Frame>,
            context: Option<&mut V8Context>,
        ) {
            let Some(context) = context else { return };
            crate::streams::install_punch_rects_binding(context);
        }
    }
}

impl KspRenderProcessHandlerBuilder {
    pub fn build() -> RenderProcessHandler {
        Self::new()
    }
}

// -----------------------------------------------------------------------
// Client: returns the render + display handlers to CEF
// -----------------------------------------------------------------------

wrap_client! {
    pub struct KspClientBuilder {
        render_handler: RenderHandler,
        display_handler: DisplayHandler,
        // Writer the punch-through handler will use to push the SHM
        // stream-rect table when the page sends a rect snapshot.
        writer: Arc<Mutex<ShmWriter>>,
    }

    impl Client {
        fn render_handler(&self) -> Option<cef::RenderHandler> {
            Some(self.render_handler.clone())
        }

        fn display_handler(&self) -> Option<cef::DisplayHandler> {
            Some(self.display_handler.clone())
        }

        /// Dispatch renderer→browser process messages by name. Our
        /// only consumer right now is the punch-through stream-rect
        /// snapshot; everything else falls through (return 0) for CEF
        /// to handle.
        fn on_process_message_received(
            &self,
            _browser: Option<&mut Browser>,
            _frame: Option<&mut Frame>,
            _source_process: ProcessId,
            message: Option<&mut ProcessMessage>,
        ) -> ::std::os::raw::c_int {
            let Some(message) = message else { return 0 };
            let name_userfree = message.name();
            let name: cef::CefStringUtf16 = (&name_userfree).into();
            let name = name.to_string();
            if name == crate::streams::PUNCH_RECTS_MSG {
                if crate::streams::handle_punch_rects_message(&self.writer, message) {
                    return 1;
                }
            }
            0
        }
    }
}

impl KspClientBuilder {
    pub fn build(render_handler: RenderHandler, writer: Arc<Mutex<ShmWriter>>) -> Client {
        let display_handler = KspDisplayHandlerBuilder::new();
        Self::new(render_handler, display_handler, writer)
    }
}

// -----------------------------------------------------------------------
// BrowserProcessHandler: creates the browser once CEF is ready
// -----------------------------------------------------------------------

#[derive(Clone)]
pub struct KspBrowserProcessHandlerInner {
    client: Client,
    url: String,
    browser_slot: BrowserSlot,
}

impl KspBrowserProcessHandlerInner {
    pub fn new(client: Client, url: impl Into<String>, browser_slot: BrowserSlot) -> Self {
        Self {
            client,
            url: url.into(),
            browser_slot,
        }
    }
}

wrap_browser_process_handler! {
    pub struct KspBrowserProcessHandlerBuilder {
        handler: KspBrowserProcessHandlerInner,
    }

    impl BrowserProcessHandler {
        /// Called once CEF is fully initialized. Per the CEF docs this
        /// is the UI thread, but under `external_message_pump=1` the
        /// UI task runner isn't bound until the embedder's first
        /// `do_message_loop_work()` call — and `on_context_initialized`
        /// fires *inside* `cef::initialize`, before the loop starts.
        /// So we deliberately do **not** register cefQuery handlers
        /// here (cef-rs's `add_handler` debug-asserts the runner is
        /// bound, and trips otherwise). The main pump loop registers
        /// them on its first tick instead.
        fn on_context_initialized(&self) {
            eprintln!("CEF context initialized; creating OSR browser");

            let window_info = WindowInfo {
                windowless_rendering_enabled: 1,
                // macOS uses the accelerated-paint IOSurface handoff
                // (zero-copy); Windows uses the CPU-side on_paint
                // callback because CEF 146's accelerated-paint shared
                // textures read as empty from the host process
                // regardless of compositor/adapter flags.
                //
                // Deliberately not setting `external_begin_frame_enabled`
                // — the cef-rs OSR example flips both together, but
                // when that flag is set CEF stops rendering until
                // something drives `send_external_begin_frame`. With
                // it off CEF free-runs at `windowless_frame_rate`
                // (60 Hz), which is what we want on both platforms.
                shared_texture_enabled: 1,
                ..Default::default()
            };

            let browser_settings = BrowserSettings {
                windowless_frame_rate: 60,
                // 0 = fully transparent. Without this CEF paints an
                // opaque white background *behind* the page, which
                // squashes any CSS-level transparency before it ever
                // reaches the shm. With this set, CEF only renders
                // what the page's CSS actually paints, so alpha=0
                // regions pass through as transparent to the plugin.
                background_color: 0,
                ..Default::default()
            };

            let url = CefString::from(self.handler.url.as_str());
            let mut client = self.handler.client.clone();

            // Sync creation so we get the Browser back and can kick it
            // out of its initial "hidden" visibility state — an OSR
            // browser with no window attached starts hidden, which
            // throttles rAF/CSS animations and means paints only fire
            // once. was_hidden(false) tells the compositor the surface
            // is visible and continuous paints resume.
            let browser = browser_host_create_browser_sync(
                Some(&window_info),
                Some(&mut client),
                Some(&url),
                Some(&browser_settings),
                None,
                None,
            );

            match browser.as_ref().and_then(|b| b.host()) {
                Some(host) => {
                    host.was_hidden(0);
                    // Tell CEF the OSR view has logical keyboard focus.
                    // Without this, Chromium drops non-character keys
                    // (arrows, Delete, Home/End, Escape) on their way
                    // to the DOM — text-typing works via the CHAR
                    // path regardless, but editor-navigation keys
                    // silently no-op. We're always the active UI, so
                    // the flag stays on for the browser's lifetime.
                    host.set_focus(1);
                    eprintln!("browser created, visibility -> shown");
                }
                None => {
                    eprintln!("ERROR: browser creation returned None");
                }
            }

            // Stash the browser so the main pump loop can dispatch
            // mouse events through BrowserHost. cef::Browser is Clone
            // via reference counting.
            if let Some(b) = browser {
                match self.handler.browser_slot.lock() {
                    Ok(mut slot) => {
                        *slot = Some(b);
                        eprintln!("browser handle stashed");
                    }
                    Err(e) => eprintln!("browser slot poisoned: {e}"),
                }
            }
        }
    }
}

impl KspBrowserProcessHandlerBuilder {
    pub fn build(inner: KspBrowserProcessHandlerInner) -> BrowserProcessHandler {
        Self::new(inner)
    }
}

// -----------------------------------------------------------------------
// App: passed to `cef::initialize`, returns the process handler
// -----------------------------------------------------------------------

#[derive(Clone)]
pub struct KspAppInner {
    /// Browser-process handler. `None` in subprocess invocations like
    /// the helper binary (which doesn't host a browser); `Some` in the
    /// main sidecar process where we actually create the browser.
    process_handler: Option<BrowserProcessHandler>,
    /// Render-process handler. Always present so the renderer-side
    /// `cefQuery` router gets installed when CEF spawns a renderer
    /// subprocess.
    render_process_handler: RenderProcessHandler,
}

impl KspAppInner {
    pub fn new(
        process_handler: Option<BrowserProcessHandler>,
        render_process_handler: RenderProcessHandler,
    ) -> Self {
        Self {
            process_handler,
            render_process_handler,
        }
    }
}

wrap_app! {
    pub struct KspAppBuilder {
        app: KspAppInner,
    }

    impl App {
        fn on_before_command_line_processing(
            &self,
            process_type: Option<&cef::CefStringUtf16>,
            command_line: Option<&mut cef::CommandLine>,
        ) {
            let Some(command_line) = command_line else { return };
            // CEF invokes this for every process role — browser,
            // renderer, gpu, utility, zygote — once we hand the same
            // App to both the main binary and the helper subprocess
            // entry point. The browser-process invocation has an
            // empty / null `process_type`; subprocesses pass their
            // role name. Apply our switches only on the browser
            // invocation. Specifically, propagating
            // `remote-debugging-port=9229` to the GPU subprocess was
            // observed to silently break `on_accelerated_paint`
            // (no frames delivered to the browser).
            let is_browser_process = match process_type {
                None => true,
                Some(s) => s.to_string().is_empty(),
            };
            if !is_browser_process {
                return;
            }
            // Quiet a few things that otherwise log at startup.
            command_line.append_switch(Some(&"no-startup-window".into()));
            command_line.append_switch(Some(&"noerrdialogs".into()));
            command_line.append_switch(Some(&"hide-crash-restore-bubble".into()));
            command_line.append_switch(Some(&"use-mock-keychain".into()));
            // Useful for debugging: remote devtools on :9229
            command_line.append_switch_with_value(
                Some(&"remote-debugging-port".into()),
                Some(&"9229".into()),
            );
            // Deliberately no GPU-related switches — cefclient's
            // Windows OSR sample and OBS's browser source both run at
            // defaults, and it's the only combination that makes
            // `OnAcceleratedPaint` produce readable pixels here
            // (the other knobs — --use-angle, --use-adapter-luid,
            // --in-process-gpu, --disable-gpu-sandbox — in any
            // combination left the shared texture empty on reads).
        }

        fn browser_process_handler(&self) -> Option<cef::BrowserProcessHandler> {
            self.app.process_handler.clone()
        }

        fn render_process_handler(&self) -> Option<cef::RenderProcessHandler> {
            Some(self.app.render_process_handler.clone())
        }
    }
}

impl KspAppBuilder {
    pub fn build(inner: KspAppInner) -> App {
        Self::new(inner)
    }
}
