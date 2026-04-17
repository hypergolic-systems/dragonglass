#[cfg(target_os = "macos")]
pub mod iosurface_bridge;

#[cfg(target_os = "macos")]
pub use iosurface_bridge::IOSurfaceBridge;
