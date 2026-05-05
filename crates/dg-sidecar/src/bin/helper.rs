//! CEF helper subprocess entry point.
//!
//! On macOS CEF spawns renderer/gpu/utility sub-processes out of the
//! helper `.app` bundles under `Contents/Frameworks/`. On Windows the
//! main sidecar process points CEF at this binary via
//! `Settings::browser_subprocess_path`. Each invocation runs one
//! subprocess role (renderer / gpu / utility / zygote) and exits.
//!
//! We pass a real `App` to `execute_process` so that *renderer*
//! subprocesses install our `KspRenderProcessHandler`, which binds
//! `window.dgUpdatePunchRects` on every new V8 context. Without this
//! the page's punch-through pump can't reach the browser process.

use cef::{args::Args, *};

use dg_sidecar::app::{KspAppBuilder, KspAppInner, KspRenderProcessHandlerBuilder};

fn main() {
    let args = Args::new();

    #[cfg(target_os = "macos")]
    let _loader = {
        let loader = library_loader::LibraryLoader::new(
            &std::env::current_exe().expect("current_exe"),
            true,
        );
        assert!(loader.load(), "failed to load CEF framework (helper)");
        loader
    };

    let _ = api_hash(sys::CEF_API_VERSION_LAST, 0);

    // Helper never hosts a browser, so no browser-process handler.
    let render_process_handler = KspRenderProcessHandlerBuilder::build();
    let app_inner = KspAppInner::new(None, render_process_handler);
    let mut app = KspAppBuilder::build(app_inner);

    execute_process(
        Some(args.as_main_args()),
        Some(&mut app),
        std::ptr::null_mut(),
    );
}
