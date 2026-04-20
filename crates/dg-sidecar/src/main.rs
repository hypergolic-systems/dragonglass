//! Main-process entry point for the Dragonglass CEF sidecar.
//!
//! Responsibilities, in order:
//! 1. Load the CEF framework from our app bundle.
//! 2. Dispatch to `execute_process` — if we were launched as a CEF
//!    subprocess (renderer/gpu/utility) it handles the run and we exit.
//!    In normal operation the helper binary catches those; this call is
//!    defensive.
//! 3. Set up a CEF-compatible NSApplication subclass on macOS.
//! 4. Open the shared-memory file and create the `ShmWriter` +
//!    `InputRingReader`.
//! 5. Build the `App`/`Client`/`RenderHandler`/`DisplayHandler` tree,
//!    giving the render handler exclusive ownership of the writer.
//! 6. Call `cef::initialize` and run the external message pump loop,
//!    draining the input ring into `BrowserHost::send_mouse_*` each
//!    tick.

use std::path::Path;
use std::sync::{Arc, Mutex};

use anyhow::Result;
use cef::{args::Args, *};

use dg_sidecar::app::{
    BrowserSlot, KspAppBuilder, KspAppInner, KspBrowserProcessHandlerBuilder,
    KspBrowserProcessHandlerInner, KspClientBuilder, KspRenderHandlerBuilder,
    KspRenderHandlerInner, INITIAL_HEIGHT, INITIAL_WIDTH,
};
use dg_shm::{shm_path_for_session, InputEvent, ShmWriter};

/// Watch the parent process and self-terminate if it disappears.
///
/// When KSP is force-quit, the C# `Application.quitting` hook never
/// fires, so `SidecarHost.Stop()` never gets to kill us. Without this
/// watchdog the sidecar leaks and the user has to Force Quit it from
/// the Dock.
#[cfg(target_os = "macos")]
fn spawn_parent_watchdog() {
    // SAFETY: getppid() is always safe.
    let original_parent = unsafe { libc::getppid() };
    std::thread::Builder::new()
        .name("parent-watchdog".into())
        .spawn(move || loop {
            std::thread::sleep(std::time::Duration::from_secs(1));
            let ppid = unsafe { libc::getppid() };
            if ppid != original_parent {
                eprintln!(
                    "parent-watchdog: parent {} gone (now reparented to {}) — exiting",
                    original_parent, ppid
                );
                std::process::exit(0);
            }
        })
        .expect("spawn parent-watchdog thread");
}

/// Spawn a background thread serving static files from `root_dir`.
/// Returns the base URL (e.g. `http://127.0.0.1:12345`).
fn start_static_server(root_dir: &Path) -> Result<String> {
    let server = tiny_http::Server::http("127.0.0.1:0")
        .map_err(|e| anyhow::anyhow!("failed to bind static file server: {e}"))?;
    let port = server.server_addr().to_ip().unwrap().port();
    let base_url = format!("http://127.0.0.1:{port}");
    let root = root_dir.to_path_buf();

    std::thread::Builder::new()
        .name("static-http".into())
        .spawn(move || {
            for request in server.incoming_requests() {
                // request.url() is the request-line target — includes
                // the query string. Truncate at the first '?' or '#'
                // before doing filesystem lookup, otherwise a load like
                // `/?ws=ws://...` tries to open a file literally named
                // `?ws=ws://...` and 404s.
                let url = request.url();
                let path_only = &url[..url.find(|c| c == '?' || c == '#').unwrap_or(url.len())];
                let url_path = path_only.trim_start_matches('/');
                let file_path = if url_path.is_empty() {
                    root.join("index.html")
                } else {
                    root.join(url_path)
                };

                // Prevent path traversal
                if !file_path.starts_with(&root) {
                    let _ = request.respond(tiny_http::Response::from_string("forbidden").with_status_code(403));
                    continue;
                }

                match std::fs::File::open(&file_path) {
                    Ok(file) => {
                        let content_type = match file_path.extension().and_then(|e| e.to_str()) {
                            Some("html") => "text/html; charset=utf-8",
                            Some("js") => "application/javascript",
                            Some("css") => "text/css",
                            Some("json") => "application/json",
                            Some("png") => "image/png",
                            Some("svg") => "image/svg+xml",
                            Some("woff2") => "font/woff2",
                            Some("woff") => "font/woff",
                            _ => "application/octet-stream",
                        };
                        let header = tiny_http::Header::from_bytes(
                            b"Content-Type", content_type.as_bytes()
                        ).unwrap();
                        let response = tiny_http::Response::from_file(file)
                            .with_header(header);
                        let _ = request.respond(response);
                    }
                    Err(_) => {
                        let _ = request.respond(
                            tiny_http::Response::from_string("not found")
                                .with_status_code(404),
                        );
                    }
                }
            }
        })?;

    Ok(base_url)
}

/// Map an `InputEvent` from the SHM ring buffer into a CEF
/// `BrowserHost::send_mouse_*` call. Resize and navigate events are
/// handled in the drain block instead — they need extra state (the
/// render handler / canvas bridge for resize, the cross-slot URL
/// accumulator and browser handle for navigate).
fn inject_input_event(host: &cef::BrowserHost, evt: InputEvent) {
    use dg_shm::layout::{
        INPUT_BTN_LEFT, INPUT_BTN_MIDDLE, INPUT_BTN_RIGHT, INPUT_MOUSE_DOWN, INPUT_MOUSE_MOVE,
        INPUT_MOUSE_UP, INPUT_MOUSE_WHEEL,
    };

    let mouse_event = cef::MouseEvent {
        x: evt.x,
        y: evt.y,
        modifiers: 0,
    };

    match evt.kind {
        INPUT_MOUSE_MOVE => {
            host.send_mouse_move_event(Some(&mouse_event), 0);
        }
        INPUT_MOUSE_DOWN | INPUT_MOUSE_UP => {
            let btn = match evt.button {
                INPUT_BTN_LEFT => cef::MouseButtonType::LEFT,
                INPUT_BTN_RIGHT => cef::MouseButtonType::RIGHT,
                INPUT_BTN_MIDDLE => cef::MouseButtonType::MIDDLE,
                _ => return,
            };
            let mouse_up = if evt.kind == INPUT_MOUSE_UP { 1 } else { 0 };
            host.send_mouse_click_event(Some(&mouse_event), btn, mouse_up, 1);
        }
        INPUT_MOUSE_WHEEL => {
            host.send_mouse_wheel_event(Some(&mouse_event), 0, evt.extra);
        }
        _ => {}
    }
}

/// In-progress `INPUT_NAVIGATE` message. The header slot establishes
/// the expected URL byte length; subsequent `INPUT_NAVIGATE_CHUNK`
/// slots append 12 bytes each until `buf.len() >= expected`. State
/// lives across drain calls because the plugin may publish header +
/// chunks across the sidecar's 8 ms sleep.
struct NavAccumulator {
    expected: usize,
    buf: Vec<u8>,
}

/// Append up to 12 bytes from a chunk slot's x / y / extra fields
/// into `buf`, capped so the final chunk's zero-padding is dropped.
fn push_nav_chunk(buf: &mut Vec<u8>, evt: &InputEvent, expected: usize) {
    let mut tmp = [0u8; 12];
    tmp[0..4].copy_from_slice(&evt.x.to_le_bytes());
    tmp[4..8].copy_from_slice(&evt.y.to_le_bytes());
    tmp[8..12].copy_from_slice(&evt.extra.to_le_bytes());
    let take = expected.saturating_sub(buf.len()).min(12);
    buf.extend_from_slice(&tmp[..take]);
}

/// Handle an `INPUT_RESIZE` event from the plugin. Recreates the
/// canvas IOSurface at the requested dims, republishes the new size
/// in the SHM header, and tells CEF to re-query `view_rect` via
/// `was_resized`. Order matters: the new canvas bridge + atomics land
/// first so the next `on_accelerated_paint` writes into a surface
/// that's already at the new size.
fn handle_resize(handler: &KspRenderHandlerInner, host: &cef::BrowserHost, evt: InputEvent) {
    if evt.x <= 0 || evt.y <= 0 {
        eprintln!("resize: ignoring invalid dims {}x{}", evt.x, evt.y);
        return;
    }
    let (w, h) = (evt.x as u32, evt.y as u32);
    eprintln!("resize → {}x{}", w, h);

    #[cfg(target_os = "macos")]
    match dg_gpu::IOSurfaceBridge::create(w, h) {
        Ok(bridge) => handler.set_io_surface_bridge(bridge),
        Err(e) => {
            eprintln!("resize: bridge recreate failed: {} — keeping old bridge", e);
            return;
        }
    }

    handler.set_size(w, h);

    if let Ok(mut writer) = handler.writer().lock() {
        writer.set_dimensions(w, h);
    }

    host.was_resized();
}

fn main() -> Result<()> {
    // CLI: positional `<path-or-url> <session-id> [ws-url]` plus
    // optional `--device-scale=<f>` anywhere. We parse flags first
    // into a separate vec so positional indexing stays simple.
    let raw: Vec<String> = std::env::args().collect();
    let mut device_scale: f32 = 1.0;
    let mut positional: Vec<String> = Vec::with_capacity(raw.len());
    // Skip argv[0]; anything matching --device-scale=<f> is consumed.
    for arg in raw.iter().skip(1) {
        if let Some(v) = arg.strip_prefix("--device-scale=") {
            match v.parse::<f32>() {
                Ok(f) if f.is_finite() && f > 0.0 => {
                    device_scale = f.clamp(0.5, 3.0);
                }
                _ => {
                    eprintln!("--device-scale: ignoring invalid value {v:?}; using 1.0");
                }
            }
        } else {
            positional.push(arg.clone());
        }
    }
    if positional.len() < 2 {
        anyhow::bail!(
            "usage: dg-sidecar <path-or-url> <session-id> [ws-url] [--device-scale=<f>]"
        );
    }
    let url_or_path = &positional[0];
    let session_id = &positional[1];
    // Optional third positional: live-telemetry WS URL. The sidecar
    // appends it to the loaded page as `?ws=<encoded>` so the UI
    // auto-connects when launched by the KSP HUD. Browser-side
    // iteration (`just ui-dev`) doesn't pass it and falls back to the
    // simulated feed.
    let ws_url = positional.get(2);
    eprintln!("device scale factor: {device_scale}");

    // If the argument is a local path, serve it over HTTP.
    // If it's already a URL, use it directly.
    let base_url = if url_or_path.starts_with("http://") || url_or_path.starts_with("https://") {
        url_or_path.clone()
    } else {
        let path = Path::new(url_or_path);
        if !path.is_dir() {
            anyhow::bail!("UI path is not a directory: {}", path.display());
        }
        let base = start_static_server(path)?;
        eprintln!("static file server → {base} (root: {})", path.display());
        base
    };

    let boot_url = match ws_url {
        Some(ws) => format!("{base_url}?ws={}", urlencoding::encode(ws)),
        None => base_url,
    };
    #[cfg(target_os = "macos")]
    let _library = {
        let loader = library_loader::LibraryLoader::new(&std::env::current_exe()?, false);
        if !loader.load() {
            anyhow::bail!("failed to load CEF framework (main process)");
        }
        loader
    };

    let _ = api_hash(sys::CEF_API_VERSION_LAST, 0);

    let args = Args::new();
    let cmd_line = args
        .as_cmd_line()
        .ok_or_else(|| anyhow::anyhow!("failed to parse command line"))?;

    // Defensive subprocess dispatch.
    let type_switch = CefString::from("type");
    let is_browser_process = cmd_line.has_switch(Some(&type_switch)) != 1;
    let ret = execute_process(
        Some(args.as_main_args()),
        None::<&mut App>,
        std::ptr::null_mut(),
    );
    if !is_browser_process {
        if ret < 0 {
            anyhow::bail!("execute_process reported failure in subprocess");
        }
        return Ok(());
    }
    debug_assert_eq!(ret, -1);

    eprintln!("main process starting");

    #[cfg(target_os = "macos")]
    spawn_parent_watchdog();

    #[cfg(target_os = "macos")]
    dg_sidecar::mac::setup_application();

    // --- Shared memory (4096 bytes: 128-byte header + input ring) ---
    let shm_path = shm_path_for_session(session_id);
    eprintln!(
        "creating shm {} ({}x{} initial + input ring)",
        shm_path.display(),
        INITIAL_WIDTH,
        INITIAL_HEIGHT
    );
    let writer = ShmWriter::create(&shm_path, INITIAL_WIDTH, INITIAL_HEIGHT)
        .map_err(|e| anyhow::anyhow!("ShmWriter::create failed: {e}"))?;
    // Input ring reader shares the same mmap as the writer.
    let input_ring = unsafe { dg_shm::InputRingReader::new(writer.mmap_base()) };

    eprintln!("loading {}", boot_url);

    // --- Browser slot: populated by on_context_initialized ---
    let browser_slot: BrowserSlot = Arc::new(Mutex::new(None));

    // --- Build CEF tree ---
    let render_inner = KspRenderHandlerInner::new(writer, device_scale);

    #[cfg(target_os = "macos")]
    {
        match dg_gpu::IOSurfaceBridge::create(INITIAL_WIDTH, INITIAL_HEIGHT) {
            Ok(bridge) => render_inner.set_io_surface_bridge(bridge),
            Err(e) => {
                eprintln!("canvas bridge init failed: {} — zero-copy disabled", e);
            }
        }
    }

    // Keep a clone of the render-handler inner for the main loop so we
    // can drive size updates + bridge swaps from the INPUT_RESIZE branch.
    // Inner is Clone and all its mutable state lives behind Arcs, so
    // both this handle and the CEF-owned handler observe the same state.
    let render_inner_main = render_inner.clone();
    let render_handler = KspRenderHandlerBuilder::build(render_inner);
    let client = KspClientBuilder::build(render_handler);
    let process_inner =
        KspBrowserProcessHandlerInner::new(client, boot_url, browser_slot.clone());
    let process_handler = KspBrowserProcessHandlerBuilder::build(process_inner);
    let app_inner = KspAppInner::new(process_handler);
    let mut app = KspAppBuilder::build(app_inner);

    // Session-scoped CEF cache so multiple sidecar instances don't
    // fight over the singleton lock.
    let cache_dir = std::env::temp_dir().join(format!("dragonglass-cef-{session_id}"));
    std::fs::create_dir_all(&cache_dir)?;
    let cache_path = CefString::from(cache_dir.to_str().unwrap());

    let settings = Settings {
        no_sandbox: 1,
        windowless_rendering_enabled: 1,
        external_message_pump: 1,
        root_cache_path: cache_path,
        ..Default::default()
    };

    let initialized = initialize(
        Some(args.as_main_args()),
        Some(&settings),
        Some(&mut app),
        std::ptr::null_mut(),
    );
    if initialized != 1 {
        anyhow::bail!("cef::initialize failed");
    }

    eprintln!("entering CEF message pump loop");

    // In-progress navigate URL accumulator. Lives across drain calls
    // because the plugin's multi-slot navigate write may straddle our
    // 8 ms sleep.
    let mut nav: Option<NavAccumulator> = None;

    // Manual pump loop. Sleep interval is 8 ms — balances latency
    // against GPU contention with Unity's Metal work.
    loop {
        do_message_loop_work();

        // Drain input events from the SHM ring buffer and inject them
        // into CEF. Resize needs the render handler (to swap the
        // canvas bridge + update atomics); navigate needs both the
        // cross-slot accumulator and the browser handle for
        // `main_frame().load_url()`. Mouse events fall through to
        // `inject_input_event`.
        if let Ok(slot) = browser_slot.lock() {
            if let Some(ref browser) = *slot {
                if let Some(host) = browser.host() {
                    use dg_shm::layout::{
                        INPUT_NAVIGATE, INPUT_NAVIGATE_CHUNK, INPUT_RESIZE, MAX_NAV_URL_BYTES,
                    };
                    input_ring.drain(|evt| match evt.kind {
                        INPUT_RESIZE => {
                            nav = None;
                            handle_resize(&render_inner_main, &host, evt);
                        }
                        INPUT_NAVIGATE => {
                            let n = (evt.extra as usize).min(MAX_NAV_URL_BYTES);
                            nav = Some(NavAccumulator {
                                expected: n,
                                buf: Vec::with_capacity(n),
                            });
                        }
                        INPUT_NAVIGATE_CHUNK => match nav.as_mut() {
                            Some(acc) => {
                                push_nav_chunk(&mut acc.buf, &evt, acc.expected);
                                if acc.buf.len() >= acc.expected {
                                    match std::str::from_utf8(&acc.buf) {
                                        Ok(url) => {
                                            if let Some(frame) = browser.main_frame() {
                                                frame.load_url(Some(&CefString::from(url)));
                                            }
                                        }
                                        Err(_) => {
                                            eprintln!("nav: dropped non-UTF-8 URL");
                                        }
                                    }
                                    nav = None;
                                }
                            }
                            None => {
                                eprintln!("nav: stray chunk without header — dropping");
                            }
                        },
                        _ => inject_input_event(&host, evt),
                    });
                }
            }
        }

        std::thread::sleep(std::time::Duration::from_millis(8));
    }

    #[allow(unreachable_code)]
    {
        shutdown();
        Ok(())
    }
}
