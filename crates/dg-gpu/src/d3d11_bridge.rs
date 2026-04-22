//! DXGI shared-NT-handle + D3D11 blitter — Windows equivalent of the
//! macOS IOSurface pipeline in `iosurface_bridge.rs`.
//!
//! ## Architecture recap
//!
//!   1. Sidecar creates ONE canvas `ID3D11Texture2D` at startup with
//!      `SHARED_NTHANDLE | SHARED_KEYEDMUTEX` misc flags, and obtains
//!      an NT shared HANDLE via `IDXGIResource1::CreateSharedHandle`.
//!   2. The HANDLE's low 32 bits are published in the SHM header
//!      (`io_surface_id` field) — Windows guarantees handles are
//!      32-bit significant (MSDN "Interprocess Communication Between
//!      32-bit and 64-bit Applications"), so the upper bits are
//!      always zero and truncation is lossless.
//!   3. Every `on_accelerated_paint` CEF hands us a HANDLE pointing
//!      at its per-frame shared D3D11 texture. We open it via
//!      `ID3D11Device1::OpenSharedResource1`, acquire CEF's keyed
//!      mutex (key 1 — CEF's viz layer releases with 1 when a frame
//!      is ready) and our canvas's keyed mutex (key 0 — initial /
//!      "plugin is done" state), `CopyResource` into canvas, release
//!      both.
//!   4. Plugin (deferred work) opens the canvas HANDLE via its own
//!      D3D11 device, acquires the canvas mutex with key 0, blits
//!      into Unity's destination texture, releases with key 0.

use std::sync::Arc;

use windows::core::{Interface, PCWSTR};
use windows::Win32::Foundation::{CloseHandle, GENERIC_ALL, HANDLE, HMODULE};
use windows::Win32::Graphics::Direct3D::{
    D3D_DRIVER_TYPE_HARDWARE, D3D_FEATURE_LEVEL, D3D_FEATURE_LEVEL_11_0,
};
use windows::Win32::Graphics::Direct3D11::{
    D3D11CreateDevice, ID3D11Device, ID3D11Device1, ID3D11DeviceContext, ID3D11Texture2D,
    D3D11_BIND_RENDER_TARGET, D3D11_BIND_SHADER_RESOURCE, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
    D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX, D3D11_RESOURCE_MISC_SHARED_NTHANDLE,
    D3D11_SDK_VERSION, D3D11_TEXTURE2D_DESC, D3D11_USAGE_DEFAULT,
};
use windows::Win32::Graphics::Dxgi::Common::{DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_SAMPLE_DESC};
use windows::Win32::Graphics::Dxgi::{IDXGIAdapter, IDXGIKeyedMutex, IDXGIResource1};

// ---------------------------------------------------------------------
// Inner handle — holds the raw NT handle so our Drop closes it.
// ---------------------------------------------------------------------

struct BridgeInner {
    _device: ID3D11Device,
    device1: ID3D11Device1,
    context: ID3D11DeviceContext,
    canvas_texture: ID3D11Texture2D,
    canvas_mutex: IDXGIKeyedMutex,
    canvas_handle: HANDLE,
}

// SAFETY: D3D11 devices/contexts created without
// D3D11_CREATE_DEVICE_SINGLETHREADED are free-threaded; the HANDLE is
// just an integer. Access is serialized at a higher level by the CEF
// paint thread.
unsafe impl Send for BridgeInner {}
unsafe impl Sync for BridgeInner {}

impl Drop for BridgeInner {
    fn drop(&mut self) {
        if !self.canvas_handle.is_invalid() {
            // SAFETY: handle produced by CreateSharedHandle; we own it.
            unsafe {
                let _ = CloseHandle(self.canvas_handle);
            }
        }
    }
}

/// Public handle. Cheap to clone — all clones share one `BridgeInner`.
#[derive(Clone)]
pub struct D3D11Bridge {
    inner: Arc<BridgeInner>,
}

impl D3D11Bridge {
    /// Allocate the D3D11 device + canvas texture + shared NT handle.
    pub fn create(width: u32, height: u32) -> Result<Self, String> {
        let (device, context) =
            create_device().map_err(|e| format!("D3D11CreateDevice failed: {e}"))?;
        let device1: ID3D11Device1 = device
            .cast()
            .map_err(|e| format!("cast ID3D11Device -> ID3D11Device1 failed: {e}"))?;

        let canvas_texture = create_canvas_texture(&device, width, height)
            .map_err(|e| format!("CreateTexture2D(canvas {width}x{height}) failed: {e}"))?;

        let canvas_mutex: IDXGIKeyedMutex = canvas_texture
            .cast()
            .map_err(|e| format!("canvas texture missing IDXGIKeyedMutex: {e}"))?;

        let canvas_handle = create_shared_handle(&canvas_texture)
            .map_err(|e| format!("CreateSharedHandle(canvas) failed: {e}"))?;

        Ok(Self {
            inner: Arc::new(BridgeInner {
                _device: device,
                device1,
                context,
                canvas_texture,
                canvas_mutex,
                canvas_handle,
            }),
        })
    }

    /// Low 32 bits of the canvas NT handle — what we publish via the
    /// SHM header's `io_surface_id` field. See module comment for the
    /// MSDN guarantee that handles are 32-bit significant.
    pub fn canvas_id(&self) -> u32 {
        self.inner.canvas_handle.0 as usize as u32
    }

    /// Full NT handle. Not used by the SHM path (which goes through
    /// `canvas_id()`) but handy for same-process consumers / tests.
    pub fn canvas_handle(&self) -> HANDLE {
        self.inner.canvas_handle
    }

    /// Blit CEF's per-frame shared texture into our canvas. Called from
    /// the Windows branch of `on_accelerated_paint`. `cef_shared_handle`
    /// is the raw `shared_texture_handle` field of `AcceleratedPaintInfo`
    /// (cef's `HANDLE` typedef is `*mut c_void`; we wrap it into a
    /// `windows::Win32::Foundation::HANDLE` internally). The handle is
    /// only valid for the duration of the callback, so we open, copy,
    /// and release within this call.
    ///
    /// CEF's shared texture uses `SHARED_NTHANDLE` without
    /// `SHARED_KEYEDMUTEX` — synchronization is implicit (the texture
    /// pool holds the producer side until the callback returns). We
    /// still acquire our own canvas mutex so the plugin-side consumer
    /// (which does use a keyed mutex) sees a consistent frame.
    pub fn blit_from_cef(
        &self,
        cef_shared_handle: *mut std::ffi::c_void,
    ) -> Result<(), String> {
        let cef_shared_handle = HANDLE(cef_shared_handle);
        if cef_shared_handle.is_invalid() {
            return Err("cef handle is invalid".into());
        }

        let cef_tex: ID3D11Texture2D = unsafe {
            self.inner
                .device1
                .OpenSharedResource1(cef_shared_handle)
                .map_err(|e| format!("OpenSharedResource1(cef) failed: {e}"))?
        };

        // Canvas starts at key 0 and toggles with the plugin. If the
        // plugin hasn't consumed yet we overwrite — acceptable, CEF
        // throttles us and we only care about the latest frame.
        let canvas_locked = unsafe {
            self.inner.canvas_mutex.AcquireSync(0, u32::MAX).is_ok()
        };

        if canvas_locked {
            unsafe {
                self.inner
                    .context
                    .CopyResource(&self.inner.canvas_texture, &cef_tex);
                let _ = self.inner.canvas_mutex.ReleaseSync(1);
            }
        }

        Ok(())
    }
}

// ---------------------------------------------------------------------
// Private helpers
// ---------------------------------------------------------------------

fn create_device() -> windows::core::Result<(ID3D11Device, ID3D11DeviceContext)> {
    let mut device: Option<ID3D11Device> = None;
    let mut context: Option<ID3D11DeviceContext> = None;
    let feature_levels: [D3D_FEATURE_LEVEL; 1] = [D3D_FEATURE_LEVEL_11_0];
    unsafe {
        D3D11CreateDevice(
            None::<&IDXGIAdapter>,
            D3D_DRIVER_TYPE_HARDWARE,
            HMODULE::default(),
            D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            Some(&feature_levels),
            D3D11_SDK_VERSION,
            Some(&mut device),
            None,
            Some(&mut context),
        )?;
    }
    Ok((device.unwrap(), context.unwrap()))
}

fn create_canvas_texture(
    device: &ID3D11Device,
    width: u32,
    height: u32,
) -> windows::core::Result<ID3D11Texture2D> {
    let desc = D3D11_TEXTURE2D_DESC {
        Width: width,
        Height: height,
        MipLevels: 1,
        ArraySize: 1,
        Format: DXGI_FORMAT_B8G8R8A8_UNORM,
        SampleDesc: DXGI_SAMPLE_DESC {
            Count: 1,
            Quality: 0,
        },
        Usage: D3D11_USAGE_DEFAULT,
        BindFlags: (D3D11_BIND_SHADER_RESOURCE.0 | D3D11_BIND_RENDER_TARGET.0) as u32,
        CPUAccessFlags: 0,
        MiscFlags: (D3D11_RESOURCE_MISC_SHARED_NTHANDLE.0
            | D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX.0) as u32,
    };
    let mut tex: Option<ID3D11Texture2D> = None;
    unsafe {
        device.CreateTexture2D(&desc, None, Some(&mut tex))?;
    }
    Ok(tex.unwrap())
}

fn create_shared_handle(texture: &ID3D11Texture2D) -> windows::core::Result<HANDLE> {
    let resource: IDXGIResource1 = texture.cast()?;
    // pattributes=None (default security), lpname=null (unnamed handle)
    unsafe { resource.CreateSharedHandle(None, GENERIC_ALL.0, PCWSTR::null()) }
}
