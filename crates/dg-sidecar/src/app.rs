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
//!   dimensions; `on_accelerated_paint` receives IOSurface handles
//!   from CEF's GPU compositor and publishes them via `ShmWriter`.
//! * `KspDisplayHandler` — forwards JS `console.*` output to stdout
//!   so the C# `SidecarHost` can route it to `UDebug.Log`.

use std::sync::atomic::{AtomicU32, AtomicU64};
use std::sync::{Arc, Mutex};

use cef::*;

use dg_shm::ShmWriter;

/// Shared slot holding the single Browser instance.
/// `on_context_initialized` populates it once CEF is ready; the main
/// pump loop reads from it to dispatch mouse events through
/// `BrowserHost::send_mouse_*`.
pub type BrowserSlot = Arc<Mutex<Option<Browser>>>;

/// Viewport size. CEF renders into an IOSurface on the GPU and the
/// plugin wraps that IOSurface as an MTLTexture via
/// `Texture2D.CreateExternalTexture` — the BGRA bytes never flow
/// through shared memory.
// Must match `DragonglassHudAddon.OverlayWidth/OverlayHeight` in the C#
// plugin AND the physical KSP window resolution for 1:1 pixel mapping
// (no aspect-ratio squish). KSP 1.12.5 on this machine runs 1920×1200
// (16:10). If you run KSP at a different resolution, update both sides
// in lockstep. TODO: swap for a runtime handshake once the plugin can
// push its Screen.width/height to the sidecar at flight-scene start.
pub const VIEWPORT_WIDTH: u32 = 1920;
pub const VIEWPORT_HEIGHT: u32 = 1200;

// -----------------------------------------------------------------------
// RenderHandler: the actual OnPaint sink
// -----------------------------------------------------------------------

/// Inner render handler state. Clone-able because the `wrap_render_handler!`
/// macro copies it internally. All mutable state lives behind `Arc<Mutex>`.
#[derive(Clone)]
pub struct KspRenderHandlerInner {
    width: u32,
    height: u32,
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
    /// Bridge that transports each unique IOSurface's mach port to the
    /// plugin side over a Unix domain socket. Optional because we only
    /// build it on macOS; kept here as an `Option` so the struct type
    /// is platform-independent even though the field is only ever
    /// populated on Darwin.
    #[cfg(target_os = "macos")]
    io_surface_bridge: Option<dg_gpu::iosurface_bridge::IOSurfaceBridge>,
}

impl KspRenderHandlerInner {
    pub fn new(writer: ShmWriter) -> Self {
        let width = writer.width();
        let height = writer.height();
        Self {
            width,
            height,
            writer: Arc::new(Mutex::new(writer)),
            frame_counter: Arc::new(AtomicU64::new(0)),
            last_io_surface_id: Arc::new(AtomicU32::new(0)),
            io_surface_gen: Arc::new(AtomicU32::new(0)),
            #[cfg(target_os = "macos")]
            io_surface_bridge: None,
        }
    }

    /// Attach the mach-port bridge that ships IOSurface handles to the
    /// plugin side. Must be called before the browser starts rendering.
    #[cfg(target_os = "macos")]
    pub fn set_io_surface_bridge(&mut self, bridge: dg_gpu::iosurface_bridge::IOSurfaceBridge) {
        self.io_surface_bridge = Some(bridge);
    }
}

wrap_render_handler! {
    pub struct KspRenderHandlerBuilder {
        handler: KspRenderHandlerInner,
    }

    impl RenderHandler {
        /// Tell CEF what size to render at. Called whenever CEF needs
        /// the viewport size — typically once per browser, plus after
        /// `BrowserHost::was_resized()` which we don't call yet.
        fn view_rect(&self, _browser: Option<&mut Browser>, rect: Option<&mut Rect>) {
            if let Some(rect) = rect {
                rect.x = 0;
                rect.y = 0;
                rect.width = self.handler.width as _;
                rect.height = self.handler.height as _;
            }
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
            let canvas_id = if let Some(bridge) = self.handler.io_surface_bridge.as_ref() {
                bridge.blit_from_cef(handle as *const _, cef_id);
                bridge.canvas_id()
            } else {
                0
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
// Client: returns the render + display handlers to CEF
// -----------------------------------------------------------------------

wrap_client! {
    pub struct KspClientBuilder {
        render_handler: RenderHandler,
        display_handler: DisplayHandler,
    }

    impl Client {
        fn render_handler(&self) -> Option<cef::RenderHandler> {
            Some(self.render_handler.clone())
        }

        fn display_handler(&self) -> Option<cef::DisplayHandler> {
            Some(self.display_handler.clone())
        }
    }
}

impl KspClientBuilder {
    pub fn build(render_handler: RenderHandler) -> Client {
        let display_handler = KspDisplayHandlerBuilder::new();
        Self::new(render_handler, display_handler)
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
        /// Called on the UI thread once CEF is fully initialized. This
        /// is the earliest safe point to create a browser.
        fn on_context_initialized(&self) {
            eprintln!("CEF context initialized; creating OSR browser");

            let window_info = WindowInfo {
                windowless_rendering_enabled: 1,
                // Zero-copy pixel pipeline: CEF renders into an IOSurface
                // on the GPU, and `on_accelerated_paint` gives us a handle
                // to it. We deliberately do NOT set
                // `external_begin_frame_enabled` — the cef-rs OSR example
                // flips both together, but when that flag is set CEF
                // stops rendering until something drives
                // `send_external_begin_frame`. With it off CEF free-runs
                // at `windowless_frame_rate` (60 Hz), which is what we
                // want.
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
    process_handler: BrowserProcessHandler,
}

impl KspAppInner {
    pub fn new(process_handler: BrowserProcessHandler) -> Self {
        Self { process_handler }
    }
}

wrap_app! {
    pub struct KspAppBuilder {
        app: KspAppInner,
    }

    impl App {
        fn on_before_command_line_processing(
            &self,
            _process_type: Option<&cef::CefStringUtf16>,
            command_line: Option<&mut cef::CommandLine>,
        ) {
            let Some(command_line) = command_line else { return };
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
        }

        fn browser_process_handler(&self) -> Option<cef::BrowserProcessHandler> {
            Some(self.app.process_handler.clone())
        }
    }
}

impl KspAppBuilder {
    pub fn build(inner: KspAppInner) -> App {
        Self::new(inner)
    }
}
