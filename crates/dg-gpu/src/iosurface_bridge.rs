//! Canvas IOSurface + Metal blitter — Rust shim over the Obj-C++
//! implementation in `src/iosurface_blitter.m`.
//!
//! ## Why this indirection
//!
//! The actual Metal + IOSurface work lives in Obj-C because
//! `metal-rs` (the Rust Metal binding) doesn't expose
//! `newTextureWithDescriptor:iosurface:plane:` and calling that
//! directly via `objc2::msg_send!` from Rust is uglier than just
//! keeping the Metal code in Obj-C. The `cc` crate compiles the
//! `.m` file into the sidecar binary at build time; this module
//! declares the small C API the `.m` file exports and wraps it in
//! a safe Rust interface.
//!
//! ## Architecture recap
//!
//!   1. Sidecar allocates ONE canvas IOSurface at startup via
//!      `IOSurfaceCreate` with `kIOSurfaceIsGlobal = true`. The
//!      deprecated-but-still-functional flag makes the surface
//!      lookupable cross-process via `IOSurfaceLookup(id)` in any
//!      process in the same user session.
//!   2. Sidecar wraps the canvas as an `MTLTexture` via
//!      `[MTLDevice newTextureWithDescriptor:iosurface:plane:]`
//!      using its own Metal device (independent of CEF's device,
//!      which we can't reach from outside CEF anyway).
//!   3. Every `on_accelerated_paint`, Rust calls `blit_from_cef`,
//!      which wraps CEF's per-frame IOSurface as a source MTLTexture
//!      (cached per CEF IOSurfaceID — CEF pools ~2 surfaces and
//!      rotates), then issues a single `MTLBlitCommandEncoder`
//!      copy from source to canvas.
//!   4. Rust publishes the canvas's stable `IOSurfaceID` in the shm
//!      header. The plugin reads the ID, calls `IOSurfaceLookup`,
//!      wraps the result as a `GL_TEXTURE_RECTANGLE` via
//!      `CGLTexImageIOSurface2D`, and blits it into Unity's
//!      destination texture.
//!
//! See `src/iosurface_blitter.m` for the Obj-C implementation.

use std::sync::Arc;

// ---------------------------------------------------------------------
// C API from iosurface_blitter.h
// ---------------------------------------------------------------------

unsafe extern "C" {
    fn dg_blitter_create(width: u32, height: u32) -> *mut std::ffi::c_void;
    fn dg_blitter_destroy(handle: *mut std::ffi::c_void);
    fn dg_blitter_canvas_id(handle: *mut std::ffi::c_void) -> u32;
    fn dg_blitter_blit_from_cef(
        handle: *mut std::ffi::c_void,
        cef_surface_handle: *const std::ffi::c_void,
        cef_surface_id: u32,
    );
}

// ---------------------------------------------------------------------
// Safe Rust wrapper
// ---------------------------------------------------------------------

struct BlitterHandle(*mut std::ffi::c_void);
// SAFETY: the Obj-C blitter is thread-safe in the way we use it —
// `blit_from_cef` only touches state owned by the blitter instance,
// and we serialize calls through the Mutex inside CEF's paint handler
// (which runs on the CEF UI thread, same thread every time). The
// canvas_id getter is a simple field read.
unsafe impl Send for BlitterHandle {}
unsafe impl Sync for BlitterHandle {}

impl Drop for BlitterHandle {
    fn drop(&mut self) {
        unsafe { dg_blitter_destroy(self.0) };
    }
}

/// Public handle. Cheap to clone — wraps an `Arc` so all clones share
/// the single Obj-C blitter instance.
#[derive(Clone)]
pub struct IOSurfaceBridge {
    inner: Arc<BlitterHandle>,
}

impl IOSurfaceBridge {
    /// Create a canvas IOSurface of `width x height` BGRA8 via the
    /// Obj-C blitter. Returns `Err` if Metal is unavailable or
    /// `IOSurfaceCreate` fails.
    pub fn create(width: u32, height: u32) -> Result<Self, String> {
        let raw = unsafe { dg_blitter_create(width, height) };
        if raw.is_null() {
            return Err(format!(
                "dg_blitter_create({}x{}) returned NULL — check sidecar stderr for specifics",
                width, height
            ));
        }
        Ok(Self {
            inner: Arc::new(BlitterHandle(raw)),
        })
    }

    /// IOSurfaceID of the canvas. Stable for the lifetime of the
    /// bridge — plugin caches its GL wrapper keyed on this.
    pub fn canvas_id(&self) -> u32 {
        unsafe { dg_blitter_canvas_id(self.inner.0) }
    }

    /// Blit CEF's current IOSurface into our canvas. Called from
    /// `on_accelerated_paint`. `cef_surface_handle` is the raw
    /// `*const c_void` from `AcceleratedPaintInfo.shared_texture_io_surface`;
    /// `cef_surface_id` is the CEF IOSurface's own ID (used as a
    /// cache key inside the blitter).
    pub fn blit_from_cef(
        &self,
        cef_surface_handle: *const std::ffi::c_void,
        cef_surface_id: u32,
    ) {
        unsafe {
            dg_blitter_blit_from_cef(self.inner.0, cef_surface_handle, cef_surface_id);
        }
    }
}
