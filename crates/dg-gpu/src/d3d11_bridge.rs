//! DXGI shared-NT-handle + D3D11 blitter — Windows equivalent of the
//! macOS IOSurface pipeline in `iosurface_bridge.rs`.
//!
//! ## Architecture
//!
//!   1. Sidecar creates ONE canvas `ID3D11Texture2D` at startup with
//!      `SHARED_NTHANDLE | SHARED_KEYEDMUTEX` misc flags, and obtains
//!      a *named* NT shared HANDLE via
//!      `IDXGIResource1::CreateSharedHandle(Some(name))`. NT handles
//!      are process-local pointers, so we can't hand the raw HANDLE
//!      to the plugin — the other process opens it by name instead,
//!      via `ID3D11Device1::OpenSharedResourceByName`.
//!   2. The name is `Local\Dragonglass-Canvas-XXXXXXXX` where
//!      XXXXXXXX is a per-bridge random u32 `canvas_id`. That id is
//!      what we publish in the SHM header's `io_surface_id` field;
//!      the plugin reconstructs the name from it. When the bridge is
//!      recreated on resize, `canvas_id` changes, which signals the
//!      plugin to invalidate its open-shared-resource cache.
//!   3. Every `on_accelerated_paint` CEF hands us a HANDLE pointing
//!      at its per-frame shared D3D11 texture. We open it via
//!      `OpenSharedResource1`, bind it as a shader-resource view,
//!      and draw a fullscreen triangle through the `UnpremulPass`
//!      shader that samples CEF's texture and writes straight-alpha
//!      pixels into the canvas RTV. We do NOT `CopyResource` from
//!      CEF's shared texture — on Windows/CEF 146 that path silently
//!      reads zero pixels even with correct dimensions, format, and
//!      adapter. Only the SRV-sampling read path surfaces CEF's
//!      actual compositor output (same technique cefclient's
//!      `tests/cefclient/browser/osr_d3d11_win.cc` uses).
//!   4. Plugin opens the canvas by name via its own D3D11 device
//!      (Unity's), AcquireSyncs the keyed mutex with key 1,
//!      `CopyResource`-s into Unity's destination texture, and
//!      releases with key 0.

use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::Arc;

use windows::core::{Interface, HSTRING, PCWSTR};
use windows::Win32::Foundation::{CloseHandle, GENERIC_ALL, HANDLE, HMODULE};
use windows::Win32::Graphics::Direct3D::{
    D3D_DRIVER_TYPE_HARDWARE, D3D_FEATURE_LEVEL, D3D_FEATURE_LEVEL_11_0,
};
use windows::Win32::Graphics::Direct3D11::{
    D3D11CreateDevice, ID3D11Device, ID3D11Device1, ID3D11DeviceContext, ID3D11RenderTargetView,
    ID3D11ShaderResourceView, ID3D11Texture2D, D3D11_BIND_RENDER_TARGET, D3D11_BIND_SHADER_RESOURCE,
    D3D11_CREATE_DEVICE_BGRA_SUPPORT, D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX,
    D3D11_RESOURCE_MISC_SHARED_NTHANDLE, D3D11_SDK_VERSION, D3D11_TEXTURE2D_DESC,
    D3D11_USAGE_DEFAULT,
};
use windows::Win32::Graphics::Dxgi::Common::{DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_SAMPLE_DESC};
use windows::Win32::Graphics::Dxgi::{IDXGIAdapter, IDXGIKeyedMutex, IDXGIResource1};

use crate::unpremul_pass::UnpremulPass;

// ---------------------------------------------------------------------
// Inner handle — holds the raw NT handle so our Drop closes it.
// ---------------------------------------------------------------------

struct BridgeInner {
    _device: ID3D11Device,
    device1: ID3D11Device1,
    context: ID3D11DeviceContext,
    // Held to keep the canvas D3D11 resource alive while the derived
    // RTV / named shared handle reference it. Never read directly.
    _canvas_texture: ID3D11Texture2D,
    canvas_mutex: IDXGIKeyedMutex,
    canvas_handle: HANDLE,
    canvas_id: u32,
    canvas_rtv: ID3D11RenderTargetView,
    unpremul: UnpremulPass,
    width: u32,
    height: u32,
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
    /// Allocate the D3D11 device + canvas texture + named shared NT
    /// handle. `canvas_id` is generated here (random-ish, non-zero, and
    /// new on every bridge creation) and drives the shared handle name
    /// both processes use.
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

        let canvas_id = next_canvas_id();
        let name = canvas_handle_name(canvas_id);
        let canvas_handle = create_shared_handle(&canvas_texture, &name)
            .map_err(|e| format!("CreateSharedHandle(canvas, {name:?}) failed: {e}"))?;

        let canvas_rtv = create_rtv(&device1, &canvas_texture)
            .map_err(|e| format!("CreateRenderTargetView(canvas): {e}"))?;

        let unpremul = UnpremulPass::create(&device1)
            .map_err(|e| format!("UnpremulPass::create: {e}"))?;

        Ok(Self {
            inner: Arc::new(BridgeInner {
                _device: device,
                device1,
                context,
                _canvas_texture: canvas_texture,
                canvas_mutex,
                canvas_handle,
                canvas_id,
                canvas_rtv,
                unpremul,
                width,
                height,
            }),
        })
    }

    /// Random-ish u32 identifying this canvas — published to the plugin
    /// via the SHM header's `io_surface_id` field. The plugin feeds it
    /// to `canvas_handle_name()` to reconstruct the shared-handle name.
    /// Changes on every `D3D11Bridge::create` (i.e. resize), which lets
    /// the plugin invalidate its open-shared-resource cache.
    pub fn canvas_id(&self) -> u32 {
        self.inner.canvas_id
    }

    /// Full NT handle. Not used by the SHM path (which goes through
    /// `canvas_id()` + named open) but handy for same-process consumers
    /// and tests.
    pub fn canvas_handle(&self) -> HANDLE {
        self.inner.canvas_handle
    }

    /// Blit CEF's per-frame shared texture into our canvas. Called from
    /// the Windows branch of `on_accelerated_paint`. `cef_shared_handle`
    /// is the raw `shared_texture_handle` field of `AcceleratedPaintInfo`
    /// (cef's `HANDLE` typedef is `*mut c_void`; we wrap it into a
    /// `windows::Win32::Foundation::HANDLE` internally). The handle is
    /// only valid for the duration of the callback, so we open, render,
    /// and release within this call.
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

        let cef_srv: ID3D11ShaderResourceView = create_srv(&self.inner.device1, &cef_tex)
            .map_err(|e| format!("CreateShaderResourceView(cef): {e}"))?;

        // Canvas keyed mutex handshake: plugin releases with key 0,
        // sidecar acquires 0 here, renders, releases with key 1 so
        // the plugin can blit into Unity's destination texture.
        let canvas_locked =
            unsafe { self.inner.canvas_mutex.AcquireSync(0, u32::MAX).is_ok() };

        if canvas_locked {
            unsafe {
                self.inner.unpremul.draw(
                    &self.inner.context,
                    &cef_srv,
                    &self.inner.canvas_rtv,
                    self.inner.width,
                    self.inner.height,
                );
                self.inner.context.Flush();
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

fn create_shared_handle(
    texture: &ID3D11Texture2D,
    name: &str,
) -> windows::core::Result<HANDLE> {
    let resource: IDXGIResource1 = texture.cast()?;
    let wide = HSTRING::from(name);
    // pattributes=None (default security); named handle so the plugin
    // in KSP's process can open it via OpenSharedResourceByName.
    unsafe { resource.CreateSharedHandle(None, GENERIC_ALL.0, PCWSTR(wide.as_ptr())) }
}

fn create_srv(
    device: &ID3D11Device1,
    texture: &ID3D11Texture2D,
) -> windows::core::Result<ID3D11ShaderResourceView> {
    let mut srv: Option<ID3D11ShaderResourceView> = None;
    unsafe {
        // Passing None for the descriptor lets D3D11 infer format +
        // MIP levels from the texture's own desc — equivalent to an
        // explicit BGRA8_UNORM single-mip view here.
        device.CreateShaderResourceView(texture, None, Some(&mut srv))?;
    }
    Ok(srv.unwrap())
}

fn create_rtv(
    device: &ID3D11Device1,
    texture: &ID3D11Texture2D,
) -> windows::core::Result<ID3D11RenderTargetView> {
    let mut rtv: Option<ID3D11RenderTargetView> = None;
    unsafe {
        device.CreateRenderTargetView(texture, None, Some(&mut rtv))?;
    }
    Ok(rtv.unwrap())
}

/// Build the Local\-namespaced shared-handle name the bridge creates
/// and the plugin opens. Must stay in sync with the Windows KSP
/// plugin's `canvas_handle_name` in `crates/dg-hud-plugin-win/src/imp.rs`.
pub fn canvas_handle_name(canvas_id: u32) -> String {
    format!("Local\\Dragonglass-Canvas-{canvas_id:08x}")
}

/// Produce a non-zero u32 unique to this bridge instance. Seeded from
/// the process id + a process-local counter so a crash-restart on the
/// sidecar produces a fresh id even if the monotonic counter resets.
fn next_canvas_id() -> u32 {
    static COUNTER: AtomicU32 = AtomicU32::new(0);
    let seq = COUNTER.fetch_add(1, Ordering::Relaxed);
    // Mix pid and a nanosecond snapshot so a plugin that has a stale
    // id from a previous sidecar generation doesn't accidentally
    // collide with a newly allocated one.
    let pid = std::process::id();
    let nanos = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.subsec_nanos())
        .unwrap_or(0);
    let mixed = pid.wrapping_mul(2654435761) ^ nanos ^ seq.wrapping_mul(0x9E3779B1);
    // Avoid 0 so consumers can treat canvas_id==0 as "no canvas yet".
    if mixed == 0 {
        1
    } else {
        mixed
    }
}
