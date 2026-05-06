//! CEF helper subprocess entry point.
//!
//! On macOS CEF spawns renderer/gpu/utility sub-processes out of the
//! helper `.app` bundles under `Contents/Frameworks/`. Each of those
//! points at THIS binary. Its only job is to load the CEF framework
//! and call `execute_process`, which runs the subprocess role and
//! returns when CEF is done with it.

use cef::{args::Args, *};

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

    execute_process(
        Some(args.as_main_args()),
        None::<&mut App>,
        std::ptr::null_mut(),
    );
}
