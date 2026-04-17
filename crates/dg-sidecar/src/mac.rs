//! Minimum macOS NSApplication boilerplate for CEF.
//!
//! CEF requires the main-process NSApplication to conform to
//! `CefAppProtocol`/`CrAppProtocol`/`CrAppControlProtocol`. Those
//! protocols are the ones the CEF framework inspects on macOS to route
//! events correctly. Without this, CEF aborts at startup with a cryptic
//! "Application does not respond to -isHandlingSendEvent" style error.
//!
//! We do NOT install an app delegate, load a MainMenu nib, or handle
//! applicationShouldTerminate. Those are relevant for a windowed app
//! with a native menu bar; the sidecar is headless and exits on SIGTERM.
//!
//! Ported from `cef-rs/examples/cefsimple/src/mac/mod.rs` (Apache-2.0 OR MIT).

use cef::application_mac::{CefAppProtocol, CrAppControlProtocol, CrAppProtocol};
use objc2::{
    define_class, extern_methods, msg_send,
    rc::Retained,
    runtime::{AnyObject, Bool, NSObjectProtocol},
    ClassType, DefinedClass, MainThreadMarker,
};
use objc2_app_kit::{NSApp, NSApplication, NSEvent};
use std::cell::Cell;

/// Instance variables of `KspWebHudApplication`.
#[derive(Default)]
pub struct KspWebHudApplicationIvars {
    handling_send_event: Cell<Bool>,
}

define_class!(
    /// An `NSApplication` subclass that implements the CEF protocols
    /// needed on macOS. Required for CEF to deliver events correctly.
    #[unsafe(super(NSApplication))]
    #[ivars = KspWebHudApplicationIvars]
    pub struct KspWebHudApplication;

    impl KspWebHudApplication {
        #[unsafe(method(sendEvent:))]
        unsafe fn send_event(&self, event: &NSEvent) {
            let was_sending_event = self.is_handling_send_event();
            if !was_sending_event {
                self.set_handling_send_event(true);
            }

            let _: () = msg_send![super(self), sendEvent: event];

            if !was_sending_event {
                self.set_handling_send_event(false);
            }
        }

        /// CEF needs to be able to clean up when `-terminate:` is called.
        /// The default NSApplication `-terminate:` ends the process with
        /// `exit()`, which doesn't let CEF shut down cleanly. For v0 we
        /// don't support orderly quit-via-menu (there is no menu), so we
        /// just drop the event — the sidecar is killed externally.
        #[unsafe(method(terminate:))]
        unsafe fn terminate(&self, _sender: &AnyObject) {
            // Intentionally a no-op. See file-level docs.
        }
    }

    unsafe impl CrAppControlProtocol for KspWebHudApplication {
        #[unsafe(method(setHandlingSendEvent:))]
        unsafe fn _set_handling_send_event(&self, handling_send_event: Bool) {
            self.ivars().handling_send_event.set(handling_send_event);
        }
    }

    unsafe impl CrAppProtocol for KspWebHudApplication {
        #[unsafe(method(isHandlingSendEvent))]
        unsafe fn _is_handling_send_event(&self) -> Bool {
            self.ivars().handling_send_event.get()
        }
    }

    unsafe impl CefAppProtocol for KspWebHudApplication {}
);

impl KspWebHudApplication {
    extern_methods! {
        #[unsafe(method(sharedApplication))]
        fn shared_application() -> Retained<Self>;

        #[unsafe(method(setHandlingSendEvent:))]
        fn set_handling_send_event(&self, handling_send_event: bool);

        #[unsafe(method(isHandlingSendEvent))]
        fn is_handling_send_event(&self) -> bool;
    }
}

/// Instantiate the CEF-compatible NSApplication. Must be called on the
/// main thread, before any code touches `NSApp`/`NSApplication`.
pub fn setup_application() {
    let _ = KspWebHudApplication::shared_application();

    // If any code touched NSApp before us, the shared application
    // would already be a plain `NSApplication` and this assertion
    // would fire.
    assert!(
        NSApp(MainThreadMarker::new().expect("Not running on the main thread"))
            .isKindOfClass(KspWebHudApplication::class())
    );
}
