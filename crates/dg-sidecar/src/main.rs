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

/// Translate a Windows virtual-key code (which is what the plugin
/// ships on the wire, regardless of host OS) into the platform-native
/// key code Chromium wants in `KeyEvent::native_key_code`.
///
/// macOS: Chromium's `keyboard_code_conversion_mac.mm` uses the NSEvent
/// scancode to synthesise the Blink key identity — feeding it `0` makes
/// every press look like the 'A' key, because NSEvent `0x00` is 'A'.
/// Return the actual Mac scancode so DOM `keydown` reports the right
/// key and editor commands (deleteContentBackward, moveCaret, …) fire.
/// Unknown VKs fall through to `0xFFFF`, which Chromium treats as
/// "no native mapping" rather than a valid scancode.
///
/// Windows: `native_key_code` carries the lParam-style scancode; for
/// our purposes passing through the VK is the safer default (Chromium's
/// Windows routing uses `windows_key_code` as the primary signal).
#[cfg(target_os = "macos")]
fn native_key_code_for(windows_vk: i32) -> i32 {
    // Windows VK → macOS NSEvent keyCode. Covers the keys we emit from
    // the plugin's `PolledKeyCodes` list (arrows / editing / function /
    // navigation) plus the letters, digits, and keypad we hand through
    // for completeness. Derived from `<HIToolbox/Events.h>` (`kVK_*`).
    match windows_vk {
        0x08 => 0x33,         // VK_BACK → Delete (Mac Backspace)
        0x09 => 0x30,         // VK_TAB
        0x0D => 0x24,         // VK_RETURN
        0x10 => 0x38,         // VK_SHIFT (left)
        0x11 => 0x3B,         // VK_CONTROL (left)
        0x12 => 0x3A,         // VK_MENU (left alt/option)
        0x13 => 0x71,         // VK_PAUSE → F15 (closest analogue)
        0x14 => 0x39,         // VK_CAPITAL
        0x1B => 0x35,         // VK_ESCAPE
        0x20 => 0x31,         // VK_SPACE
        0x21 => 0x74,         // VK_PRIOR (PageUp)
        0x22 => 0x79,         // VK_NEXT  (PageDown)
        0x23 => 0x77,         // VK_END
        0x24 => 0x73,         // VK_HOME
        0x25 => 0x7B,         // VK_LEFT
        0x26 => 0x7E,         // VK_UP
        0x27 => 0x7C,         // VK_RIGHT
        0x28 => 0x7D,         // VK_DOWN
        0x2D => 0x72,         // VK_INSERT → Help on Mac layouts
        0x2E => 0x75,         // VK_DELETE (forward)
        0x30 => 0x1D,         // 0
        0x31 => 0x12,         // 1
        0x32 => 0x13,         // 2
        0x33 => 0x14,         // 3
        0x34 => 0x15,         // 4
        0x35 => 0x17,         // 5
        0x36 => 0x16,         // 6
        0x37 => 0x1A,         // 7
        0x38 => 0x1C,         // 8
        0x39 => 0x19,         // 9
        0x41 => 0x00,         // A
        0x42 => 0x0B,         // B
        0x43 => 0x08,         // C
        0x44 => 0x02,         // D
        0x45 => 0x0E,         // E
        0x46 => 0x03,         // F
        0x47 => 0x05,         // G
        0x48 => 0x04,         // H
        0x49 => 0x22,         // I
        0x4A => 0x26,         // J
        0x4B => 0x28,         // K
        0x4C => 0x25,         // L
        0x4D => 0x2E,         // M
        0x4E => 0x2D,         // N
        0x4F => 0x1F,         // O
        0x50 => 0x23,         // P
        0x51 => 0x0C,         // Q
        0x52 => 0x0F,         // R
        0x53 => 0x01,         // S
        0x54 => 0x11,         // T
        0x55 => 0x20,         // U
        0x56 => 0x09,         // V
        0x57 => 0x0D,         // W
        0x58 => 0x07,         // X
        0x59 => 0x10,         // Y
        0x5A => 0x06,         // Z
        0x5B => 0x37,         // VK_LWIN → Cmd
        0x5C => 0x36,         // VK_RWIN → right Cmd
        0x60..=0x69 => match windows_vk {
            0x60 => 0x52, 0x61 => 0x53, 0x62 => 0x54, 0x63 => 0x55,
            0x64 => 0x56, 0x65 => 0x57, 0x66 => 0x58, 0x67 => 0x59,
            0x68 => 0x5B, 0x69 => 0x5C, _ => 0xFFFF,
        },
        0x70..=0x7B => match windows_vk {
            0x70 => 0x7A, 0x71 => 0x78, 0x72 => 0x63, 0x73 => 0x76,
            0x74 => 0x60, 0x75 => 0x61, 0x76 => 0x62, 0x77 => 0x64,
            0x78 => 0x65, 0x79 => 0x6D, 0x7A => 0x67, 0x7B => 0x6F,
            _ => 0xFFFF,
        },
        0xA0 => 0x38,         // VK_LSHIFT
        0xA1 => 0x3C,         // VK_RSHIFT
        0xA2 => 0x3B,         // VK_LCONTROL
        0xA3 => 0x3E,         // VK_RCONTROL
        0xA4 => 0x3A,         // VK_LMENU (left alt)
        0xA5 => 0x3D,         // VK_RMENU (right alt)
        0xBA => 0x29,         // ;:
        0xBB => 0x18,         // =+
        0xBC => 0x2B,         // ,<
        0xBD => 0x1B,         // -_
        0xBE => 0x2F,         // .>
        0xBF => 0x2C,         // /?
        0xC0 => 0x32,         // `~
        0xDB => 0x21,         // [
        0xDC => 0x2A,         // \
        0xDD => 0x1E,         // ]
        0xDE => 0x27,         // '
        _ => 0xFFFF,
    }
}

#[cfg(target_os = "windows")]
fn native_key_code_for(windows_vk: i32) -> i32 {
    // On Windows Chromium reads the lParam-style scancode from
    // native_key_code; the VK in windows_key_code is the primary
    // routing signal. Passing the VK through here is fine — Chromium
    // only uses native_key_code for disambiguation (left vs right
    // shift, numpad vs top-row digits) which we don't distinguish
    // from Unity's KeyCode anyway.
    windows_vk
}

#[cfg(not(any(target_os = "macos", target_os = "windows")))]
fn native_key_code_for(_windows_vk: i32) -> i32 {
    0
}

/// Translate a Windows VK into the `character` / `unmodified_character`
/// that CEF expects when the plugin has no Unity `Event.character` to
/// hand us (our `Input.GetKeyDown`-based polling can't surface the
/// character for non-text keys).
///
/// This matters because CEF's OSR key path reads these fields to
/// build the `NativeWebKeyboardEvent` text / unmodified-text strings,
/// and Blink's editor-command routing keys off them. Leaving them at
/// zero causes the documented "Backspace deletes two characters / held
/// arrows never stop repeating" bugs (CEF forum thread 11650): the
/// browser's RAWKEYDOWN and KEYUP both look like "press with no
/// character" which Chromium handles as two synthetic presses rather
/// than a single press + release.
///
/// Returning `(0, 0)` means we have no correction for this VK — pass
/// through whatever the plugin sent (including real text characters
/// it extracted from `Input.inputString`).
#[cfg(target_os = "macos")]
fn mac_character_for(windows_vk: i32) -> Option<u16> {
    // Windows VK → macOS NSEvent character (Cocoa NSFunctionKey PUA
    // values + legacy ASCII controls). Derived from
    // `<AppKit/NSEvent.h>` (NSDeleteCharacter, NS*ArrowFunctionKey,
    // NSPageUpFunctionKey, …).
    let c: u16 = match windows_vk {
        0x08 => 0x7F,   // VK_BACK → NSDeleteCharacter
        0x09 => 0x09,   // VK_TAB
        0x0D => 0x0D,   // VK_RETURN
        0x1B => 0x1B,   // VK_ESCAPE
        0x21 => 0xF72C, // VK_PRIOR (PageUp)   → NSPageUpFunctionKey
        0x22 => 0xF72D, // VK_NEXT  (PageDown) → NSPageDownFunctionKey
        0x23 => 0xF72B, // VK_END              → NSEndFunctionKey
        0x24 => 0xF729, // VK_HOME             → NSHomeFunctionKey
        0x25 => 0xF702, // VK_LEFT             → NSLeftArrowFunctionKey
        0x26 => 0xF700, // VK_UP               → NSUpArrowFunctionKey
        0x27 => 0xF703, // VK_RIGHT            → NSRightArrowFunctionKey
        0x28 => 0xF701, // VK_DOWN             → NSDownArrowFunctionKey
        0x2D => 0xF746, // VK_INSERT           → NSHelpFunctionKey
        0x2E => 0xF728, // VK_DELETE (forward) → NSDeleteFunctionKey
        0x70..=0x7B => 0xF704 + (windows_vk - 0x70) as u16, // F1..F12
        _ => return None,
    };
    Some(c)
}

#[cfg(not(target_os = "macos"))]
fn mac_character_for(_windows_vk: i32) -> Option<u16> {
    None
}

/// Map an `InputEvent` from the SHM ring buffer into a CEF
/// `BrowserHost::send_mouse_*` call. Resize and navigate events are
/// handled in the drain block instead — they need extra state (the
/// render handler / canvas bridge for resize, the cross-slot URL
/// accumulator and browser handle for navigate).
///
/// Coordinate space. The plugin writes mouse coords in **physical
/// pixels** (Unity's Screen.width / Input.mousePosition, mapped into
/// the overlay's physical extent — which matches the CEF viewport
/// size we tell the plugin on resize). But CEF's OSR input API
/// expects **DIP / CSS pixels**, because we divide physical dims by
/// `device_scale_factor` in `view_rect` / `screen_info` so the page
/// sees a DIP-sized canvas with a `devicePixelRatio` equal to the
/// scale. So we divide the incoming physical coords by the scale
/// factor before handing them to CEF — without this, on a 2× Retina
/// display every click lands at 2× the cursor's actual DIP position
/// and routes to an element off-screen (or nothing at all).
fn inject_input_event(host: &cef::BrowserHost, evt: InputEvent, device_scale: f32) {
    use dg_shm::layout::{
        INPUT_BTN_LEFT, INPUT_BTN_MIDDLE, INPUT_BTN_RIGHT, INPUT_KEY_CHAR, INPUT_KEY_DOWN,
        INPUT_KEY_UP, INPUT_MOUSE_DOWN, INPUT_MOUSE_MOVE, INPUT_MOUSE_UP, INPUT_MOUSE_WHEEL,
        KEY_MOD_ALT, KEY_MOD_CONTROL, KEY_MOD_META, KEY_MOD_SHIFT, MOUSE_HELD_LEFT,
        MOUSE_HELD_MIDDLE, MOUSE_HELD_RIGHT,
    };

    // CEF's `cef_event_flags_t` bit values — the cef Rust crate doesn't
    // expose named constants for these in 146, and they've been stable
    // in CEF for years.
    const EVENTFLAG_SHIFT_DOWN: u32 = 1 << 1;
    const EVENTFLAG_CONTROL_DOWN: u32 = 1 << 2;
    const EVENTFLAG_ALT_DOWN: u32 = 1 << 3;
    const EVENTFLAG_LEFT_MOUSE_BUTTON: u32 = 1 << 4;
    const EVENTFLAG_MIDDLE_MOUSE_BUTTON: u32 = 1 << 5;
    const EVENTFLAG_RIGHT_MOUSE_BUTTON: u32 = 1 << 6;
    const EVENTFLAG_COMMAND_DOWN: u32 = 1 << 7;

    // Keyboard events. The plugin emits KEYDOWN (with VK + modifiers +
    // character), optionally followed by CHAR, then KEYUP on release —
    // matching CEF's RAWKEYDOWN → CHAR → KEYUP contract. Setting
    // `character` on KEYDOWN (not just CHAR) matters on macOS: that's
    // how Chromium's editor-command dispatch recognises Backspace /
    // Delete / Enter and fires deleteContentBackward / insertParagraph.
    if evt.kind == INPUT_KEY_DOWN || evt.kind == INPUT_KEY_UP {
        let mut cef_mods: u32 = 0;
        if evt.y & KEY_MOD_SHIFT != 0 {
            cef_mods |= EVENTFLAG_SHIFT_DOWN;
        }
        if evt.y & KEY_MOD_CONTROL != 0 {
            cef_mods |= EVENTFLAG_CONTROL_DOWN;
        }
        if evt.y & KEY_MOD_ALT != 0 {
            cef_mods |= EVENTFLAG_ALT_DOWN;
        }
        if evt.y & KEY_MOD_META != 0 {
            cef_mods |= EVENTFLAG_COMMAND_DOWN;
        }
        let character = (evt.extra as u32 & 0xFFFF) as u16;
        let native = native_key_code_for(evt.x);
        // CEF requires character / unmodified_character populated for
        // editor keys (Backspace, arrows, …) — zeroes cause Chromium
        // to treat each press as two synthetic presses, the
        // double-delete / stuck-arrow-repeat bug documented in
        // magpcss CEF forum thread 11650. Prefer the character the
        // plugin sent (from Unity's `Event.character` on text keys);
        // otherwise fall back to the VK-derived macOS NSEvent
        // character for known editor keys.
        let effective_character = if character != 0 {
            character
        } else {
            mac_character_for(evt.x).unwrap_or(0)
        };
        let key_event = cef::KeyEvent {
            type_: if evt.kind == INPUT_KEY_DOWN {
                cef::KeyEventType::RAWKEYDOWN
            } else {
                cef::KeyEventType::KEYUP
            },
            modifiers: cef_mods,
            windows_key_code: evt.x,
            native_key_code: native,
            character: effective_character,
            unmodified_character: effective_character,
            focus_on_editable_field: 1,
            ..Default::default()
        };
        host.send_key_event(Some(&key_event));
        return;
    }
    if evt.kind == INPUT_KEY_CHAR {
        // UTF-16 code unit in the low 16 bits of `extra`. Typed char
        // events don't need key-code routing or modifier hints —
        // Unity's Event.character has already resolved layout / shift
        // / IME into the final character.
        let key_event = cef::KeyEvent {
            type_: cef::KeyEventType::CHAR,
            character: (evt.extra as u32 & 0xFFFF) as u16,
            unmodified_character: (evt.extra as u32 & 0xFFFF) as u16,
            focus_on_editable_field: 1,
            ..Default::default()
        };
        host.send_key_event(Some(&key_event));
        return;
    }

    let scale = device_scale.max(0.1);
    let dip_x = (evt.x as f32 / scale).round() as i32;
    let dip_y = (evt.y as f32 / scale).round() as i32;

    // Held-button bitmask — plugin packs it into `extra` on every
    // non-wheel mouse event. Chromium reads these flags from
    // MouseEvent.modifiers to distinguish a drag from free hover; a
    // text-selection drag fails silently without them.
    let mouse_modifiers = if evt.kind != INPUT_MOUSE_WHEEL {
        let mut m = 0u32;
        if evt.extra & MOUSE_HELD_LEFT != 0 {
            m |= EVENTFLAG_LEFT_MOUSE_BUTTON;
        }
        if evt.extra & MOUSE_HELD_RIGHT != 0 {
            m |= EVENTFLAG_RIGHT_MOUSE_BUTTON;
        }
        if evt.extra & MOUSE_HELD_MIDDLE != 0 {
            m |= EVENTFLAG_MIDDLE_MOUSE_BUTTON;
        }
        m
    } else {
        0
    };

    let mouse_event = cef::MouseEvent {
        x: dip_x,
        y: dip_y,
        modifiers: mouse_modifiers,
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

    #[cfg(target_os = "windows")]
    match dg_gpu::D3D11Bridge::create(w, h) {
        Ok(bridge) => handler.set_d3d11_bridge(bridge),
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

    #[cfg(target_os = "windows")]
    {
        match dg_gpu::D3D11Bridge::create(INITIAL_WIDTH, INITIAL_HEIGHT) {
            Ok(bridge) => render_inner.set_d3d11_bridge(bridge),
            Err(e) => {
                eprintln!("d3d11 bridge init failed: {} — zero-copy disabled", e);
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

    #[allow(unused_mut)]
    let mut settings = Settings {
        no_sandbox: 1,
        windowless_rendering_enabled: 1,
        external_message_pump: 1,
        root_cache_path: cache_path,
        ..Default::default()
    };

    // Windows has no helper .app bundle, so CEF by default uses the
    // main exe for renderer/gpu/utility subprocesses. The main exe
    // parses our URL + session-id positional args up front and bails
    // before hitting the `execute_process` defensive dispatch below,
    // so subprocess launches would fail with "UI path is not a
    // directory: --type=gpu-process". Point CEF at the dedicated
    // helper exe instead — its `main()` goes straight to
    // `execute_process`.
    #[cfg(target_os = "windows")]
    {
        let exe = std::env::current_exe()?;
        let helper = exe
            .parent()
            .ok_or_else(|| anyhow::anyhow!("sidecar exe has no parent dir"))?
            .join("dg-sidecar-helper.exe");
        let helper_str = helper
            .to_str()
            .ok_or_else(|| anyhow::anyhow!("helper path is not valid UTF-8"))?;
        settings.browser_subprocess_path = CefString::from(helper_str);
    }

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
                    let device_scale = render_inner_main.device_scale();
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
                        _ => inject_input_event(&host, evt, device_scale),
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
