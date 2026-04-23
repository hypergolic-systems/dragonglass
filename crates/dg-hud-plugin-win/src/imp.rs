//! Windows implementation of `DgHudNative.dll`. Mirrors the C API of
//! `mod/native/darwin-universal/src/DgHudNative.mm` but uses D3D11 +
//! DXGI shared NT handles instead of IOSurface + Metal/GL.
//!
//! Thread model:
//!   * `UnityPluginLoad` / `UnityPluginUnload` run on Unity's main
//!     thread at plugin load/unload.
//!   * `OnGraphicsDeviceEvent` fires on Unity's render thread when
//!     the graphics device is created / reset / destroyed.
//!   * `on_render_event` (the render-thread callback returned by
//!     `DgHudNative_GetRenderEventFunc`) also runs on Unity's render
//!     thread. Safe to touch `g_unity_context` only here.
//!   * All `DgHudNative_*` exports except `GetRenderEventFunc` run
//!     on Unity's main thread from C#. Safe to touch `g_sampler_*`
//!     but NOT `g_unity_context`.

#![allow(non_snake_case, non_upper_case_globals, clippy::missing_safety_doc)]

use std::ffi::{c_char, c_void};
use std::sync::atomic::{AtomicI32, AtomicPtr, AtomicU32, AtomicU64, Ordering};
use std::sync::Mutex;

use dg_gpu::canvas_handle_name;
use windows::core::{Interface, HSTRING, PCWSTR};
use windows::Win32::Foundation::{GENERIC_ALL, HMODULE};
use windows::Win32::Graphics::Direct3D::{
    D3D_DRIVER_TYPE_HARDWARE, D3D_FEATURE_LEVEL, D3D_FEATURE_LEVEL_11_0,
};
use windows::Win32::Graphics::Direct3D11::{
    D3D11CreateDevice, ID3D11Device, ID3D11Device1, ID3D11DeviceContext, ID3D11Resource,
    ID3D11Texture2D, D3D11_BOX, D3D11_CPU_ACCESS_READ, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
    D3D11_MAP_READ, D3D11_SDK_VERSION, D3D11_TEXTURE2D_DESC, D3D11_USAGE_STAGING,
};
use windows::Win32::Graphics::Dxgi::Common::{DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_SAMPLE_DESC};
use windows::Win32::Graphics::Dxgi::{IDXGIAdapter, IDXGIKeyedMutex};

// ---------------------------------------------------------------------
// Unity FFI (repr-C structs mirroring IUnityInterface.h / IUnityGraphics.h
// and the D3D11 header we don't have vendored but whose GUID is stable).
// ---------------------------------------------------------------------

#[repr(C)]
#[derive(Clone, Copy)]
struct UnityInterfaceGUID {
    high: u64,
    low: u64,
}

#[repr(C)]
struct IUnityInterfaces {
    GetInterface: unsafe extern "system" fn(UnityInterfaceGUID) -> *mut c_void,
    RegisterInterface: unsafe extern "system" fn(UnityInterfaceGUID, *mut c_void),
    GetInterfaceSplit: unsafe extern "system" fn(u64, u64) -> *mut c_void,
    RegisterInterfaceSplit: unsafe extern "system" fn(u64, u64, *mut c_void),
}

#[repr(C)]
#[allow(dead_code)]
enum UnityGfxRenderer {
    Null = 4,
    OpenGLCore = 17,
    D3D11 = 2,
    D3D12 = 18,
    OpenGLES20 = 8,
    OpenGLES30 = 11,
    Metal = 16,
    Vulkan = 21,
}

#[repr(i32)]
#[allow(dead_code)]
#[derive(Clone, Copy)]
enum UnityGfxDeviceEventType {
    Initialize = 0,
    Shutdown = 1,
    BeforeReset = 2,
    AfterReset = 3,
}

type DeviceEventCallback = extern "system" fn(UnityGfxDeviceEventType);

#[repr(C)]
struct IUnityGraphics {
    GetRenderer: unsafe extern "system" fn() -> i32,
    RegisterDeviceEventCallback: unsafe extern "system" fn(DeviceEventCallback),
    UnregisterDeviceEventCallback: unsafe extern "system" fn(DeviceEventCallback),
    ReserveEventIDRange: unsafe extern "system" fn(i32) -> i32,
}

#[repr(C)]
struct IUnityGraphicsD3D11 {
    GetDevice: unsafe extern "system" fn() -> *mut c_void, // ID3D11Device*
    // Other methods exist on the real interface; we only need GetDevice
    // so leaving the rest unlisted keeps our struct safely smaller than
    // the real vtable (we never dereference the tail).
}

// GUIDs from Unity's public headers. Must match bit-for-bit.
const GUID_IUNITY_GRAPHICS: UnityInterfaceGUID = UnityInterfaceGUID {
    high: 0x7CBA_0A9C_A4DD_B544,
    low: 0x8C5A_D492_6EB1_7B11,
};
const GUID_IUNITY_GRAPHICS_D3D11: UnityInterfaceGUID = UnityInterfaceGUID {
    high: 0xAAB3_7EF8_7A87_D748,
    low: 0xBF76_967F_33A5_067A,
};

// ---------------------------------------------------------------------
// Global state.
// ---------------------------------------------------------------------

// Backend tag reported via DgHudNative_GetBackend. Value 3 for D3D11.
// Matches NativeBridge.cs MapGraphicsDeviceToBackend.
const BACKEND_D3D11: i32 = 3;

static BACKEND: AtomicI32 = AtomicI32::new(0); // 0 = Unknown until Load

// Raw Unity pointers stashed in UnityPluginLoad. Access only from
// lifecycle / render-thread callbacks.
static UNITY_INTERFACES: AtomicPtr<IUnityInterfaces> = AtomicPtr::new(std::ptr::null_mut());
static UNITY_GRAPHICS: AtomicPtr<IUnityGraphics> = AtomicPtr::new(std::ptr::null_mut());
static UNITY_D3D11: AtomicPtr<IUnityGraphicsD3D11> = AtomicPtr::new(std::ptr::null_mut());

// Unity's D3D11 device + immediate context, cached after we see the
// Initialize device event. Held by OnceLock-like pattern via Mutex.
struct UnityGpu {
    device1: ID3D11Device1,
    context: ID3D11DeviceContext,
}
static UNITY_GPU: Mutex<Option<UnityGpu>> = Mutex::new(None);

// Destination texture handed to us by C#. Raw pointer interpreted as
// `ID3D11Texture2D*`; Unity owns the lifetime. We do NOT AddRef.
static DEST_TEX_PTR: AtomicPtr<c_void> = AtomicPtr::new(std::ptr::null_mut());
static DEST_W: AtomicU32 = AtomicU32::new(0);
static DEST_H: AtomicU32 = AtomicU32::new(0);

// Pending `(handle_lo32, gen)`. Same packing as the mac plugin:
//   packed = (gen << 32) | handle_lo32
static PENDING_PACKED: AtomicU64 = AtomicU64::new(0);
static LAST_COMPLETED: AtomicU64 = AtomicU64::new(0);

// Opened-canvas cache for the blit path (lives on Unity's device).
// Keyed by handle_lo32; invalidated when that changes.
struct BlitCache {
    handle_lo32: u32,
    canvas: ID3D11Texture2D,
    mutex: IDXGIKeyedMutex,
    width: u32,
    height: u32,
}
static BLIT_CACHE: Mutex<Option<BlitCache>> = Mutex::new(None);

// Separate sampler device + per-handle cache (lives on our own device
// so DgHudNative_SamplePixel never touches Unity's context).
struct SamplerGpu {
    _device1: ID3D11Device1,
    context: ID3D11DeviceContext,
    // Single 1x1 staging texture reused for every sample.
    staging: ID3D11Texture2D,
}
static SAMPLER_GPU: Mutex<Option<SamplerGpu>> = Mutex::new(None);

struct SamplerCache {
    handle_lo32: u32,
    canvas: ID3D11Texture2D,
    mutex: IDXGIKeyedMutex,
}
static SAMPLER_CACHE: Mutex<Option<SamplerCache>> = Mutex::new(None);

// Diagnostic counters.
static STAT_BLITS: AtomicU64 = AtomicU64::new(0);
static STAT_ERRORS: AtomicU64 = AtomicU64::new(0);
static STAT_CACHE_MISSES: AtomicU64 = AtomicU64::new(0);
static ERR_NO_DEVICE: AtomicU64 = AtomicU64::new(0);
static ERR_OPEN_SHARED: AtomicU64 = AtomicU64::new(0);
static ERR_NO_MUTEX: AtomicU64 = AtomicU64::new(0);
static ERR_ACQUIRE: AtomicU64 = AtomicU64::new(0);
static ERR_NO_DEST: AtomicU64 = AtomicU64::new(0);

static LAST_ERROR: Mutex<String> = Mutex::new(String::new());

// ---------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------

fn log(msg: &str) {
    use std::io::Write;
    let mut err = std::io::stderr().lock();
    let _ = writeln!(err, "[DgHudNative] {msg}");
    let _ = err.flush();
}

fn set_last_error(msg: impl Into<String>) {
    let msg = msg.into();
    log(&format!("last_error set: {msg}"));
    if let Ok(mut guard) = LAST_ERROR.lock() {
        *guard = msg;
    }
}

fn pack_pending(canvas_id: u32, gen: u32) -> u64 {
    ((gen as u64) << 32) | (canvas_id as u64)
}

fn unpack_pending(packed: u64) -> (u32, u32) {
    (packed as u32, (packed >> 32) as u32)
}

// Open the shared canvas on `device1` by the name derived from
// `canvas_id`. NT handles are process-local, so we can't pass the raw
// HANDLE across; the sidecar creates a *named* shared handle and the
// plugin opens it by the same name.
fn open_shared_canvas(
    device1: &ID3D11Device1,
    canvas_id: u32,
) -> windows::core::Result<(ID3D11Texture2D, IDXGIKeyedMutex)> {
    let name = canvas_handle_name(canvas_id);
    let wide = HSTRING::from(name);
    let tex: ID3D11Texture2D =
        unsafe { device1.OpenSharedResourceByName(PCWSTR(wide.as_ptr()), GENERIC_ALL.0)? };
    let mutex: IDXGIKeyedMutex = tex.cast()?;
    Ok((tex, mutex))
}

fn tex_size(tex: &ID3D11Texture2D) -> (u32, u32) {
    let mut desc = D3D11_TEXTURE2D_DESC::default();
    unsafe { tex.GetDesc(&mut desc) };
    (desc.Width, desc.Height)
}

// Create our own D3D11 device for pixel sampling. Called from the
// render thread inside Initialize so the adapter choice matches
// Unity's device.
fn create_sampler_gpu() -> windows::core::Result<SamplerGpu> {
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
    let device = device.unwrap();
    let device1: ID3D11Device1 = device.cast()?;
    let context = context.unwrap();

    // One 1x1 BGRA staging texture, reused for every sample.
    let desc = D3D11_TEXTURE2D_DESC {
        Width: 1,
        Height: 1,
        MipLevels: 1,
        ArraySize: 1,
        Format: DXGI_FORMAT_B8G8R8A8_UNORM,
        SampleDesc: DXGI_SAMPLE_DESC {
            Count: 1,
            Quality: 0,
        },
        Usage: D3D11_USAGE_STAGING,
        BindFlags: 0,
        CPUAccessFlags: D3D11_CPU_ACCESS_READ.0 as u32,
        MiscFlags: 0,
    };
    let mut staging: Option<ID3D11Texture2D> = None;
    unsafe {
        device1.CreateTexture2D(&desc, None, Some(&mut staging))?;
    }
    Ok(SamplerGpu {
        _device1: device1,
        context,
        staging: staging.unwrap(),
    })
}

// ---------------------------------------------------------------------
// Graphics device event + Unity plugin lifecycle
// ---------------------------------------------------------------------

extern "system" fn on_graphics_device_event(event_type: UnityGfxDeviceEventType) {
    let event_type = event_type as i32;
    match event_type {
        x if x == UnityGfxDeviceEventType::Initialize as i32 => {
            if let Err(e) = grab_unity_device() {
                set_last_error(format!("Initialize: grab_unity_device failed: {e}"));
            } else {
                log("Initialize: Unity D3D11 device cached");
            }
            // Create sampler GPU lazily on first Initialize.
            if SAMPLER_GPU.lock().map(|g| g.is_none()).unwrap_or(true) {
                match create_sampler_gpu() {
                    Ok(sampler) => {
                        *SAMPLER_GPU.lock().unwrap() = Some(sampler);
                        log("sampler D3D11 device created");
                    }
                    Err(e) => {
                        set_last_error(format!("sampler create failed: {e}"));
                    }
                }
            }
            BACKEND.store(BACKEND_D3D11, Ordering::Release);
        }
        x if x == UnityGfxDeviceEventType::Shutdown as i32 => {
            *UNITY_GPU.lock().unwrap() = None;
            *BLIT_CACHE.lock().unwrap() = None;
            // Sampler resources are fine to keep; they don't depend
            // on Unity's device.
            BACKEND.store(0, Ordering::Release);
            log("Shutdown: released Unity D3D11 device");
        }
        _ => {}
    }
}

fn grab_unity_device() -> Result<(), String> {
    let d3d11_ptr = UNITY_D3D11.load(Ordering::Acquire);
    if d3d11_ptr.is_null() {
        return Err("IUnityGraphicsD3D11 interface unavailable".into());
    }
    let get_device = unsafe { (*d3d11_ptr).GetDevice };
    let raw = unsafe { get_device() };
    if raw.is_null() {
        return Err("IUnityGraphicsD3D11::GetDevice returned null".into());
    }
    // Wrap raw ID3D11Device* without AddRef'ing (Unity owns it); the
    // from_raw constructor takes ownership of a refcount. We AddRef
    // first so our RAII handle has its own count.
    let device: ID3D11Device = unsafe {
        let device_ptr = raw as *mut c_void;
        // SAFETY: pointer is non-null and is a valid ID3D11Device
        // created by Unity for the duration of the graphics device.
        let iface = ID3D11Device::from_raw_borrowed(&device_ptr).ok_or("null device")?;
        iface.clone()
    };
    let device1: ID3D11Device1 = device
        .cast()
        .map_err(|e| format!("ID3D11Device -> ID3D11Device1 cast failed: {e}"))?;
    let context = unsafe { device1.GetImmediateContext() }
        .map_err(|e| format!("GetImmediateContext failed: {e}"))?;
    *UNITY_GPU.lock().unwrap() = Some(UnityGpu { device1, context });
    Ok(())
}

#[no_mangle]
pub unsafe extern "system" fn UnityPluginLoad(unity_interfaces: *mut c_void) {
    if unity_interfaces.is_null() {
        log("UnityPluginLoad: IUnityInterfaces* is null");
        return;
    }
    let unity_interfaces = unity_interfaces as *mut IUnityInterfaces;
    UNITY_INTERFACES.store(unity_interfaces, Ordering::Release);

    let get_interface = (*unity_interfaces).GetInterface;
    let graphics_ptr = get_interface(GUID_IUNITY_GRAPHICS) as *mut IUnityGraphics;
    let d3d11_ptr = get_interface(GUID_IUNITY_GRAPHICS_D3D11) as *mut IUnityGraphicsD3D11;
    UNITY_GRAPHICS.store(graphics_ptr, Ordering::Release);
    UNITY_D3D11.store(d3d11_ptr, Ordering::Release);

    if graphics_ptr.is_null() {
        log("UnityPluginLoad: IUnityGraphics interface unavailable");
        return;
    }
    if d3d11_ptr.is_null() {
        log("UnityPluginLoad: IUnityGraphicsD3D11 interface unavailable");
        return;
    }

    let renderer = ((*graphics_ptr).GetRenderer)();
    log(&format!(
        "UnityPluginLoad: renderer={renderer} (expect kUnityGfxRendererD3D11=2)"
    ));

    ((*graphics_ptr).RegisterDeviceEventCallback)(on_graphics_device_event);
    // Unity only fires Initialize at device create time; if our plugin
    // is loaded late (DllImport-resolved on first P/Invoke), we miss
    // that. Synthesize one so we grab the device immediately.
    on_graphics_device_event(UnityGfxDeviceEventType::Initialize);
}

#[no_mangle]
pub unsafe extern "system" fn UnityPluginUnload() {
    log(&format!(
        "UnityPluginUnload (blits={} errors={})",
        STAT_BLITS.load(Ordering::Relaxed),
        STAT_ERRORS.load(Ordering::Relaxed)
    ));
    let graphics_ptr = UNITY_GRAPHICS.load(Ordering::Acquire);
    if !graphics_ptr.is_null() {
        ((*graphics_ptr).UnregisterDeviceEventCallback)(on_graphics_device_event);
    }
    *UNITY_GPU.lock().unwrap() = None;
    *BLIT_CACHE.lock().unwrap() = None;
    *SAMPLER_GPU.lock().unwrap() = None;
    *SAMPLER_CACHE.lock().unwrap() = None;
    DEST_TEX_PTR.store(std::ptr::null_mut(), Ordering::Release);
    BACKEND.store(0, Ordering::Release);
}

// ---------------------------------------------------------------------
// Render-thread callback — the actual D3D11 blit
// ---------------------------------------------------------------------

extern "system" fn on_render_event(_event_id: i32) {
    let packed = PENDING_PACKED.load(Ordering::Acquire);
    if packed == 0 {
        return;
    }
    if packed == LAST_COMPLETED.load(Ordering::Relaxed) {
        return;
    }
    let (handle_lo32, _gen) = unpack_pending(packed);
    if handle_lo32 == 0 {
        return;
    }

    let dest_raw = DEST_TEX_PTR.load(Ordering::Acquire);
    if dest_raw.is_null() {
        ERR_NO_DEST.fetch_add(1, Ordering::Relaxed);
        return;
    }

    let unity_guard = match UNITY_GPU.lock() {
        Ok(g) => g,
        Err(_) => {
            ERR_NO_DEVICE.fetch_add(1, Ordering::Relaxed);
            return;
        }
    };
    let unity = match unity_guard.as_ref() {
        Some(u) => u,
        None => {
            ERR_NO_DEVICE.fetch_add(1, Ordering::Relaxed);
            return;
        }
    };

    let mut cache_guard = match BLIT_CACHE.lock() {
        Ok(g) => g,
        Err(_) => return,
    };

    // Invalidate cache if handle changed (sidecar recreated its canvas
    // on resize).
    if let Some(entry) = cache_guard.as_ref() {
        if entry.handle_lo32 != handle_lo32 {
            *cache_guard = None;
            STAT_CACHE_MISSES.fetch_add(1, Ordering::Relaxed);
        }
    }

    if cache_guard.is_none() {
        match open_shared_canvas(&unity.device1, handle_lo32) {
            Ok((canvas, mutex)) => {
                let (width, height) = tex_size(&canvas);
                *cache_guard = Some(BlitCache {
                    handle_lo32,
                    canvas,
                    mutex,
                    width,
                    height,
                });
            }
            Err(e) => {
                ERR_OPEN_SHARED.fetch_add(1, Ordering::Relaxed);
                STAT_ERRORS.fetch_add(1, Ordering::Relaxed);
                set_last_error(format!("OpenSharedResource1 failed: {e}"));
                return;
            }
        }
    }

    let entry = cache_guard.as_ref().unwrap();

    // Acquire with key 1 — sidecar releases with key 1 after each
    // paint. Zero-timeout (WAIT_ABANDONED / timeout = WAIT_TIMEOUT on
    // contention): blocking the render thread here hangs all of
    // Unity because the main thread waits on the render thread
    // during frame submission — a few frames after the first miss
    // the whole game loop freezes. Skipping the frame and retrying
    // next tick is the only safe behavior; the sidecar will have
    // painted by then (or it won't, and the HUD just shows the
    // previous frame's pixels). This matches the mac plugin, which
    // can't deadlock because IOSurface has no keyed-mutex contract.
    if unsafe { entry.mutex.AcquireSync(1, 0) }.is_err() {
        ERR_ACQUIRE.fetch_add(1, Ordering::Relaxed);
        return;
    }

    unsafe {
        // dest_raw is the ID3D11Texture2D* that Texture2D.GetNativeTexturePtr()
        // returned. Wrap it as a borrowed Interface — Unity owns the refcount.
        let dest_ptr = dest_raw as *mut c_void;
        let dest = ID3D11Texture2D::from_raw_borrowed(&dest_ptr);
        if let Some(dest) = dest {
            let src_res: ID3D11Resource = entry.canvas.cast().unwrap();
            let dst_res: ID3D11Resource = dest.cast().unwrap();
            unity.context.CopyResource(&dst_res, &src_res);
        }
    }

    let _ = unsafe { entry.mutex.ReleaseSync(0) };

    STAT_BLITS.fetch_add(1, Ordering::Relaxed);
    LAST_COMPLETED.store(packed, Ordering::Release);

    // Keep the guards alive past the blit.
    drop(cache_guard);
    drop(unity_guard);
}

// ---------------------------------------------------------------------
// Exported C API — parity with DgHudNative.mm
// ---------------------------------------------------------------------

#[no_mangle]
pub extern "system" fn DgHudNative_IsReady() -> i32 {
    // Treat the probe as ready once the C# side has told us its
    // backend. Unity doesn't call UnityPluginLoad on DLLs loaded via
    // Mono's DllImport resolver (which is how we get loaded from
    // GameData/Dragonglass_Hud/Plugins/), so UNITY_GPU isn't populated
    // until SetTargetTexture arrives — if we gated IsReady on UNITY_GPU
    // the initial probe would always fail, and the C# addon would
    // never even try to hand us the texture. Actual blit readiness is
    // checked per-frame in on_render_event.
    if BACKEND.load(Ordering::Acquire) == BACKEND_D3D11 {
        1
    } else {
        0
    }
}

#[no_mangle]
pub extern "system" fn DgHudNative_GetBackend() -> i32 {
    BACKEND.load(Ordering::Acquire)
}

#[no_mangle]
pub extern "system" fn DgHudNative_SetBackend(backend: i32) {
    if backend != BACKEND_D3D11 {
        log(&format!(
            "SetBackend: {backend} requested but Windows only supports D3D11 (3)"
        ));
        return;
    }
    BACKEND.store(BACKEND_D3D11, Ordering::Release);
    // Create the sampler GPU here — it's independent of Unity's
    // device and we need it ready by the time SamplePixel fires.
    if SAMPLER_GPU.lock().map(|g| g.is_none()).unwrap_or(true) {
        match create_sampler_gpu() {
            Ok(sampler) => {
                *SAMPLER_GPU.lock().unwrap() = Some(sampler);
                log("sampler D3D11 device created (SetBackend)");
            }
            Err(e) => set_last_error(format!("sampler create failed: {e}")),
        }
    }
}

#[no_mangle]
pub unsafe extern "system" fn DgHudNative_SetTargetTexture(
    native_tex: *mut c_void,
    width: i32,
    height: i32,
) {
    DEST_TEX_PTR.store(native_tex, Ordering::Release);
    DEST_W.store(width.max(0) as u32, Ordering::Release);
    DEST_H.store(height.max(0) as u32, Ordering::Release);
    log(&format!(
        "SetTargetTexture: handle={native_tex:?} {width}x{height}"
    ));

    // Derive Unity's D3D11 device from the dest texture. We can't
    // rely on UnityPluginLoad -> IUnityGraphicsD3D11::GetDevice
    // (see IsReady comment), but every ID3D11DeviceChild knows its
    // device via GetDevice — same device Unity renders with.
    if native_tex.is_null() {
        return;
    }
    let tex_ptr = native_tex;
    let tex = match ID3D11Texture2D::from_raw_borrowed(&tex_ptr) {
        Some(t) => t,
        None => return,
    };
    let device = match tex.GetDevice() {
        Ok(d) => d,
        Err(e) => {
            set_last_error(format!("ID3D11Texture2D::GetDevice failed: {e}"));
            return;
        }
    };
    let device1: ID3D11Device1 = match device.cast() {
        Ok(d) => d,
        Err(e) => {
            set_last_error(format!("ID3D11Device -> ID3D11Device1 cast failed: {e}"));
            return;
        }
    };
    let context = match device1.GetImmediateContext() {
        Ok(c) => c,
        Err(e) => {
            set_last_error(format!("GetImmediateContext failed: {e}"));
            return;
        }
    };
    *UNITY_GPU.lock().unwrap() = Some(UnityGpu { device1, context });
    // Invalidate any cached shared-canvas open on a stale device.
    *BLIT_CACHE.lock().unwrap() = None;
    log("UNITY_GPU populated from target texture");
}

#[no_mangle]
pub extern "system" fn DgHudNative_UpdatePending(handle_lo32: u32, gen: u32) {
    PENDING_PACKED.store(pack_pending(handle_lo32, gen), Ordering::Release);
}

#[no_mangle]
pub extern "system" fn DgHudNative_GetRenderEventFunc() -> extern "system" fn(i32) {
    on_render_event
}

#[no_mangle]
pub unsafe extern "system" fn DgHudNative_GetSourceSize(
    handle_lo32: u32,
    out_w: *mut u32,
    out_h: *mut u32,
) -> i32 {
    if out_w.is_null() || out_h.is_null() || handle_lo32 == 0 {
        return 0;
    }
    if let Ok(cache) = BLIT_CACHE.lock() {
        if let Some(entry) = cache.as_ref() {
            if entry.handle_lo32 == handle_lo32 {
                *out_w = entry.width;
                *out_h = entry.height;
                return 1;
            }
        }
    }
    // Fallback: open once on the sampler device to learn dimensions.
    let sampler_guard = match SAMPLER_GPU.lock() {
        Ok(g) => g,
        Err(_) => return 0,
    };
    let sampler = match sampler_guard.as_ref() {
        Some(s) => s,
        None => return 0,
    };
    let device1 = match sampler._device1.cast::<ID3D11Device1>() {
        Ok(d) => d,
        Err(_) => return 0,
    };
    match open_shared_canvas(&device1, handle_lo32) {
        Ok((canvas, _)) => {
            let (w, h) = tex_size(&canvas);
            *out_w = w;
            *out_h = h;
            1
        }
        Err(_) => 0,
    }
}

#[no_mangle]
pub extern "system" fn DgHudNative_GetBackingScale() -> f32 {
    // No equivalent of NSWindow.backingScaleFactor on Windows — caller
    // falls back to Screen.dpi / 96. Returning 0.0 signals "unavailable"
    // per the mac contract.
    0.0
}

#[no_mangle]
pub unsafe extern "system" fn DgHudNative_SamplePixel(
    handle_lo32: u32,
    x: i32,
    y: i32,
    out_bgra: *mut u32,
) -> i32 {
    if out_bgra.is_null() || handle_lo32 == 0 || x < 0 || y < 0 {
        return 0;
    }
    *out_bgra = 0;

    let sampler_guard = match SAMPLER_GPU.lock() {
        Ok(g) => g,
        Err(_) => return 0,
    };
    let sampler = match sampler_guard.as_ref() {
        Some(s) => s,
        None => return 0,
    };

    // Refresh sampler-side cache if handle changed.
    let mut cache_guard = match SAMPLER_CACHE.lock() {
        Ok(g) => g,
        Err(_) => return 0,
    };
    if let Some(entry) = cache_guard.as_ref() {
        if entry.handle_lo32 != handle_lo32 {
            *cache_guard = None;
        }
    }
    if cache_guard.is_none() {
        let device1 = match sampler._device1.cast::<ID3D11Device1>() {
            Ok(d) => d,
            Err(_) => return 0,
        };
        match open_shared_canvas(&device1, handle_lo32) {
            Ok((canvas, mutex)) => {
                *cache_guard = Some(SamplerCache {
                    handle_lo32,
                    canvas,
                    mutex,
                });
            }
            Err(_) => return 0,
        }
    }
    let entry = cache_guard.as_ref().unwrap();

    // Zero-timeout — never block the UI thread. On contention the
    // caller treats "no sample" as alpha=0 (click passes through).
    if entry.mutex.AcquireSync(1, 0).is_err() {
        return 0;
    }

    let src_box = D3D11_BOX {
        left: x as u32,
        top: y as u32,
        front: 0,
        right: x as u32 + 1,
        bottom: y as u32 + 1,
        back: 1,
    };
    let src_res: ID3D11Resource = match entry.canvas.cast() {
        Ok(r) => r,
        Err(_) => {
            let _ = entry.mutex.ReleaseSync(0);
            return 0;
        }
    };
    let dst_res: ID3D11Resource = match sampler.staging.cast() {
        Ok(r) => r,
        Err(_) => {
            let _ = entry.mutex.ReleaseSync(0);
            return 0;
        }
    };
    sampler
        .context
        .CopySubresourceRegion(&dst_res, 0, 0, 0, 0, &src_res, 0, Some(&src_box));

    let _ = entry.mutex.ReleaseSync(0);

    // Map the 1x1 staging texture for read.
    let mut mapped = windows::Win32::Graphics::Direct3D11::D3D11_MAPPED_SUBRESOURCE::default();
    if sampler
        .context
        .Map(&dst_res, 0, D3D11_MAP_READ, 0, Some(&mut mapped))
        .is_err()
    {
        return 0;
    }
    let bgra = if mapped.pData.is_null() {
        0u32
    } else {
        *(mapped.pData as *const u32)
    };
    sampler.context.Unmap(&dst_res, 0);

    *out_bgra = bgra;
    1
}

#[no_mangle]
pub unsafe extern "system" fn DgHudNative_GetStats(
    out_blits: *mut u64,
    out_errors: *mut u64,
    out_cache_misses: *mut u64,
) {
    if !out_blits.is_null() {
        *out_blits = STAT_BLITS.load(Ordering::Relaxed);
    }
    if !out_errors.is_null() {
        *out_errors = STAT_ERRORS.load(Ordering::Relaxed);
    }
    if !out_cache_misses.is_null() {
        *out_cache_misses = STAT_CACHE_MISSES.load(Ordering::Relaxed);
    }
}

#[no_mangle]
pub unsafe extern "system" fn DgHudNative_GetErrorBreakdown(
    out_no_ctx: *mut u64,
    out_iosurface_lookup: *mut u64,
    out_cgl_tex_image: *mut u64,
    out_fbo_incomplete: *mut u64,
    out_no_dest: *mut u64,
) {
    // Reuse the five-slot shape but repurpose the slots for D3D11
    // failure modes. C# doesn't attach meaning to individual field
    // names — it logs them as a row in KSP.log.
    //
    // Slot mapping:
    //   out_no_ctx            -> ERR_NO_DEVICE (no Unity D3D11 device)
    //   out_iosurface_lookup  -> ERR_OPEN_SHARED (OpenSharedResource1 failed)
    //   out_cgl_tex_image     -> ERR_NO_MUTEX (IDXGIKeyedMutex cast failed)
    //   out_fbo_incomplete    -> ERR_ACQUIRE (AcquireSync failed)
    //   out_no_dest           -> ERR_NO_DEST (dest texture not set)
    if !out_no_ctx.is_null() {
        *out_no_ctx = ERR_NO_DEVICE.load(Ordering::Relaxed);
    }
    if !out_iosurface_lookup.is_null() {
        *out_iosurface_lookup = ERR_OPEN_SHARED.load(Ordering::Relaxed);
    }
    if !out_cgl_tex_image.is_null() {
        *out_cgl_tex_image = ERR_NO_MUTEX.load(Ordering::Relaxed);
    }
    if !out_fbo_incomplete.is_null() {
        *out_fbo_incomplete = ERR_ACQUIRE.load(Ordering::Relaxed);
    }
    if !out_no_dest.is_null() {
        *out_no_dest = ERR_NO_DEST.load(Ordering::Relaxed);
    }
}

#[no_mangle]
pub unsafe extern "system" fn DgHudNative_GetLastError(buf: *mut c_char, buf_len: i32) {
    if buf.is_null() || buf_len <= 0 {
        return;
    }
    let guard = match LAST_ERROR.lock() {
        Ok(g) => g,
        Err(_) => return,
    };
    let bytes = guard.as_bytes();
    let cap = (buf_len as usize).saturating_sub(1);
    let n = bytes.len().min(cap);
    for i in 0..n {
        *buf.add(i) = bytes[i] as c_char;
    }
    *buf.add(n) = 0;
}

// Silence unused warnings on the enum — Unity sometimes hands us
// values outside the listed range (custom event types); we keep the
// full enum for documentation parity with IUnityGraphics.h.
#[allow(dead_code)]
fn _force_use_unity_gfx_renderer_enum(r: UnityGfxRenderer) -> i32 {
    r as i32
}

