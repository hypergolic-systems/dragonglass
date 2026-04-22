#[cfg(target_os = "macos")]
pub mod iosurface_bridge;

#[cfg(target_os = "macos")]
pub use iosurface_bridge::IOSurfaceBridge;

#[cfg(target_os = "windows")]
pub mod d3d11_bridge;

#[cfg(target_os = "windows")]
pub use d3d11_bridge::D3D11Bridge;
