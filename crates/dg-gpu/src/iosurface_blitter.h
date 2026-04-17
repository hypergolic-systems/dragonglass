// C API for the Obj-C++ IOSurface blitter in iosurface_blitter.m.
// Rust side calls these via `extern "C"` declarations in
// iosurface_bridge.rs.

#pragma once
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

// Create a new blitter with a canvas IOSurface of the given size.
// Returns an opaque handle (a retained Obj-C instance), or NULL on
// failure (Metal unavailable, IOSurfaceCreate failed, etc.).
void* dg_blitter_create(uint32_t width, uint32_t height);

// Release the blitter and its canvas IOSurface.
void dg_blitter_destroy(void* handle);

// Return the IOSurfaceID of the canvas. The plugin looks this up via
// `IOSurfaceLookup(id)` in its own process.
uint32_t dg_blitter_canvas_id(void* handle);

// Blit CEF's per-frame IOSurface into our canvas. `cef_surface_handle`
// is the raw void* from `AcceleratedPaintInfo.shared_texture_io_surface`;
// `cef_surface_id` is the 32-bit IOSurfaceID used to cache per-surface
// MTLTexture wrappers.
void dg_blitter_blit_from_cef(void* handle,
                              void* cef_surface_handle,
                              uint32_t cef_surface_id);

#ifdef __cplusplus
}
#endif
