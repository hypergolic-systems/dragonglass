// Dragonglass HUD plugin entry point.
//
// Runs from MainMenu onwards and persists across every scene via
// DontDestroyOnLoad — the UI app (App.svelte) reads the live `game`
// topic and decides per-scene what, if anything, to render. KSP sees
// our overlay the entire session; the CEF-side Svelte code gates the
// actual pixels. Keeping the overlay always-mounted avoids the
// tear-down/rebuild churn of a scene-scoped addon and means the
// shm/native handshake happens exactly once per session.
//
// The sidecar is started by SidecarBootstrap before this addon
// loads, so CEF is warmed up by the time we first want to paint.
//
// Pixel pipeline: zero-copy only. The sidecar writes an IOSurfaceID
// into the shm header; the plugin reads the ID each frame and calls
// the native rendering dylib to wrap the IOSurface as an MTLTexture.
// Unity's RawImage samples the same GPU memory CEF wrote — no CPU
// pixel copies.

using System;
using UnityEngine;

namespace Dragonglass.Hud
{
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class DragonglassHudAddon : MonoBehaviour
    {
        private const string LogPrefix = "[Dragonglass/Hud] ";

        private OverlayCanvas _overlay;
        private ShmReader _reader;

        // Last (width, height) we emitted as an InputResize. KSP on
        // macOS only changes resolution at Settings → Apply moments —
        // there's no live window-drag feed that would need debouncing,
        // so we just diff against this and emit on any change.
        private int _lastSentWidth = -1;
        private int _lastSentHeight = -1;
        // Last IoSurfaceGen we reacted to — detecting a roll is how we
        // notice the sidecar just published a new canvas surface.
        private uint _lastSeenGen;

        private bool _zeroCopyAvailable;
        private int _zeroCopyBackend;
        private System.IntPtr _renderEventFunc;
        private uint _currentIoSurfaceId;
        private uint _currentIoSurfaceGen;
        private ulong _lastBlitCount;

        // Shared state: advances on both paths.
        private long _lastAppliedFrame;
        private int _framesApplied;
        private float _lastFpsReportTime;

        private Vector3 _lastMousePosition;
        private bool _hasLastMouse;
        // True while a mouse button is held that we forwarded to CEF.
        // Mouse-up only forwards if the matching down was forwarded.
        private bool _cefOwnsClick;

        // Hides the stock navball sphere on Start and restores it in
        // OnDestroy. Instance-per-addon; nothing persists across scenes.
        private NavBallHider _navBallHider;

        private float _nextShmRetryTime;
        private const float ShmRetryInterval = 0.5f;

        private void Awake()
        {
            // Persist across scene transitions so the HUD is mounted
            // from MainMenu through Flight, Space Center, VAB, etc.
            // The UI app decides per-scene what to render.
            DontDestroyOnLoad(gameObject);

            // Re-hide the stock navball each time KSP enters Flight —
            // KSP rebuilds the NavBall singleton on every Flight scene
            // load, so our cached renderer references go stale. Also
            // release state on exit to Flight so the next entry starts
            // fresh.
            GameEvents.onLevelWasLoadedGUIReady.Add(OnSceneReady);
        }

        private void Start()
        {
            Debug.Log(LogPrefix + "addon loaded (" + HighLogic.LoadedScene + ")");

            SidecarHost.EnsureRunning();

            // Seed the overlay from the current window size. If the
            // sidecar is still at its bootstrap 1920×1200 by the time
            // we blit, the first frames will briefly stretch — the
            // plugin emits an InputResize shortly after and the
            // sidecar catches up.
            int initialW = Mathf.Max(1, Screen.width);
            int initialH = Mathf.Max(1, Screen.height);

            try
            {
                _overlay = new OverlayCanvas(initialW, initialH);
                // Preload with the B3 test pattern so we get *something*
                // on screen even if the sidecar isn't running.
                _overlay.ApplyBgra(BuildTestPattern(initialW, initialH));
                Debug.Log(LogPrefix + "overlay canvas ready (" + initialW + "x" + initialH + ")");
            }
            catch (System.Exception e)
            {
                Debug.LogError(LogPrefix + "overlay init failed: " + e);
                return;
            }

            int backend = 0;
            string probeErr;
            if (NativeBridge.TryProbe(SystemInfo.graphicsDeviceType, out probeErr, out backend))
            {
                _zeroCopyBackend = backend;
                _renderEventFunc = NativeBridge.DgHudNative_GetRenderEventFunc();
            }
            else
            {
                Debug.LogError(LogPrefix + "native probe failed: " + probeErr);
                return;
            }

            // First shm open attempt. If the sidecar is still booting
            // this will fail; Update() retries on a timer until the
            // file exists and matches our dimensions.
            TryOpenReader(firstAttempt: true);

            _navBallHider = new NavBallHider();
        }

        private void OnSceneReady(GameScenes scene)
        {
            if (_navBallHider == null) return;

            if (scene == GameScenes.FLIGHT)
            {
                // Deferred a beat so the NavBall singleton finishes its
                // own Start() — the renderers may not exist yet on the
                // first Flight-scene tick.
                Invoke(nameof(HideStockNavBall), 0.5f);
            }
            else
            {
                // Releasing clears cached (now-stale) refs so the next
                // Flight entry starts fresh. Safe across scene unloads:
                // Restore null-guards every cached reference.
                _navBallHider.Restore();
            }
        }

        private void HideStockNavBall()
        {
            _navBallHider?.TryHide();
        }

        /// <summary>
        /// Open the shm reader and wire the zero-copy path. Called
        /// from Start() (first attempt) and again from Update() on a
        /// slow retry timer until it succeeds — that lets
        /// `SidecarHost.EnsureRunning()` boot CEF in the background
        /// without blocking Unity's main thread.
        /// </summary>
        private void TryOpenReader(bool firstAttempt)
        {
            string shmPath = ShmReader.DefaultPath();
            try
            {
                // Dimensions are now runtime-negotiated: the shm header's
                // width/height reflect the sidecar's *current* IOSurface
                // size, not a fixed contract. Sanity already lives in
                // ShmReader.Open (magic, version, w/h > 0, stride =
                // w*4) — nothing more to check here.
                _reader = ShmReader.Open(shmPath);
                Debug.Log(LogPrefix + "shm open: " + shmPath +
                          " (" + _reader.Width + "x" + _reader.Height + ")");
            }
            catch (System.Exception e)
            {
                _reader = null;
                // Only log on the first attempt; retry path is noisy
                // otherwise while we wait for CEF to come up.
                if (firstAttempt)
                {
                    Debug.LogWarning(LogPrefix + "no sidecar shm at " + shmPath + " (" + e.Message + ")");
                }
                return;
            }

            System.IntPtr dest = _overlay.GetNativeTexturePtr();
            if (dest == System.IntPtr.Zero)
            {
                Debug.LogWarning(LogPrefix + "overlay texture has no native handle yet");
            }
            else
            {
                NativeBridge.DgHudNative_SetTargetTexture(dest, _overlay.Width, _overlay.Height);
                _zeroCopyAvailable = true;
                Debug.Log(LogPrefix + "zero-copy path active [" +
                    NativeBridge.BackendName(_zeroCopyBackend) +
                    "] dest=" + dest.ToString("x") +
                    " renderEvent=" + _renderEventFunc.ToString("x"));
            }
        }

        /// <summary>
        /// Tear down and recreate the overlay canvas at new dims,
        /// rebinding it to the native blit target. Called after the
        /// sidecar confirms a resize by publishing an IOSurface at the
        /// new size — never on the plugin's own initiative, so the
        /// blit is always size-matched.
        /// </summary>
        private void RecreateOverlay(int width, int height)
        {
            Debug.Log(LogPrefix + "resize overlay " +
                      _overlay.Width + "x" + _overlay.Height +
                      " -> " + width + "x" + height);
            _overlay.Dispose();
            _overlay = new OverlayCanvas(width, height);

            System.IntPtr dest = _overlay.GetNativeTexturePtr();
            if (dest == System.IntPtr.Zero)
            {
                Debug.LogWarning(LogPrefix + "new overlay has no native handle; " +
                                 "zero-copy path offline until next frame");
                return;
            }
            NativeBridge.DgHudNative_SetTargetTexture(dest, width, height);
        }

        /// <summary>
        /// Diff <c>Screen.width/height</c> against the last size we
        /// emitted and fire an <see cref="Layout.InputResize"/> event
        /// whenever they change. No-op until the shm is open —
        /// resize events need the ring buffer.
        /// </summary>
        private void MaybeEmitResize()
        {
            if (_reader == null) return;

            int sw = Mathf.Max(1, Screen.width);
            int sh = Mathf.Max(1, Screen.height);
            if (sw == _lastSentWidth && sh == _lastSentHeight) return;

            _reader.WriteInputEvent(Layout.InputResize, Layout.InputBtnNone, sw, sh);
            _lastSentWidth = sw;
            _lastSentHeight = sh;
            Debug.Log(LogPrefix + "resize request -> " + sw + "x" + sh);
        }

        private void Update()
        {
            // --- Lazy shm (re-)open while the sidecar is booting. ---
            if (_reader == null && Time.realtimeSinceStartup >= _nextShmRetryTime)
            {
                _nextShmRetryTime = Time.realtimeSinceStartup + ShmRetryInterval;
                TryOpenReader(firstAttempt: false);
            }

            // --- Input forwarding: KSP → CEF via SHM ring buffer ---
            if (_reader != null)
            {
                SampleAndForwardMouse();
                MaybeEmitResize();
            }

            // --- Pixel pipeline: sidecar → KSP overlay ---
            if (_reader != null && _overlay != null)
            {
                if (_zeroCopyAvailable)
                {
                    ShmReader.HeaderSnapshot snap;
                    if (_reader.TryReadHeader(out snap) && snap.IoSurfaceId != 0)
                    {
                        // A gen roll means the sidecar just published a
                        // different canvas IOSurface. Before we hand the
                        // new id to the render thread, make sure our
                        // destination texture is the same size — if the
                        // roll was caused by a resize, rebuild the
                        // overlay so the blit is 1:1.
                        if (snap.IoSurfaceGen != _lastSeenGen)
                        {
                            _lastSeenGen = snap.IoSurfaceGen;
                            uint srcW, srcH;
                            if (NativeBridge.DgHudNative_GetSourceSize(
                                    snap.IoSurfaceId, out srcW, out srcH) == 1
                                && (srcW != (uint)_overlay.Width ||
                                    srcH != (uint)_overlay.Height))
                            {
                                RecreateOverlay((int)srcW, (int)srcH);
                            }
                        }

                        NativeBridge.DgHudNative_UpdatePending(
                            snap.IoSurfaceId, snap.IoSurfaceGen);
                        if (_renderEventFunc != System.IntPtr.Zero)
                        {
                            GL.IssuePluginEvent(_renderEventFunc, 0);
                        }

                        _currentIoSurfaceId = snap.IoSurfaceId;
                        _currentIoSurfaceGen = snap.IoSurfaceGen;
                        _overlay.SetRaycastSurface(snap.IoSurfaceId);

                        if (snap.FrameId != _lastAppliedFrame)
                        {
                            _lastAppliedFrame = snap.FrameId;
                            _framesApplied++;
                        }
                    }
                }
            }

            // Log applied-fps + native-blit stats + window focus once
            // per second. If `applied fps` doesn't advance, either the
            // sidecar stopped writing or our Update() isn't ticking.
            // Native blit errors break down into the per-failure
            // counters in the DgHudNative side.
            if (Time.realtimeSinceStartup - _lastFpsReportTime >= 1f)
            {
                ulong blits, errors, misses;
                NativeBridge.DgHudNative_GetStats(out blits, out errors, out misses);
                ulong perSec = blits - _lastBlitCount;
                _lastBlitCount = blits;
                ulong eCtx, eLookup, eCgl, eFbo, eNoDest;
                NativeBridge.DgHudNative_GetErrorBreakdown(
                    out eCtx, out eLookup, out eCgl, out eFbo, out eNoDest);
                string pathStats = "id=0x" + _currentIoSurfaceId.ToString("x8") +
                    " gen=" + _currentIoSurfaceGen +
                    " blits/s=" + perSec +
                    " err=" + errors +
                    " (ctx=" + eCtx +
                    " lookup=" + eLookup +
                    " cgl=" + eCgl +
                    " fbo=" + eFbo +
                    " nodest=" + eNoDest +
                    ")" +
                    " miss=" + misses;

                Debug.Log(LogPrefix + "applied " + _framesApplied +
                    " fps [zc/" + NativeBridge.BackendName(_zeroCopyBackend) + "] " +
                    pathStats +
                    " fg=" + (Application.isFocused ? "1" : "0") +
                    " (last frame " + _lastAppliedFrame + ")");

                _framesApplied = 0;
                _lastFpsReportTime = Time.realtimeSinceStartup;
            }
        }

        /// <summary>
        /// Read the current mouse state from Unity and forward events
        /// to the sidecar via the SHM input ring buffer. Coordinate
        /// mapping: Unity is bottom-left origin (y up); CEF wants
        /// top-left origin (y down).
        ///
        /// Hit-testing: mouse-down only forwards to CEF if the pixel
        /// under the cursor has alpha > 0 (i.e. a HUD panel is there).
        /// Transparent regions pass through to KSP.
        /// </summary>
        private void SampleAndForwardMouse()
        {
            Vector3 mouse = Input.mousePosition;

            int screenW = Screen.width;
            int screenH = Screen.height;
            if (screenW <= 0 || screenH <= 0) return;

            // Coordinate scaling uses the current overlay dims (which
            // match CEF's viewport after the resize handshake). During
            // the brief resize transition these may disagree with
            // Screen.width/height — the mouse hit-test is briefly
            // offset, but the window isn't receiving meaningful input
            // mid-resize anyway.
            int overlayW = _overlay != null ? _overlay.Width : screenW;
            int overlayH = _overlay != null ? _overlay.Height : screenH;
            int cefX = (int)((mouse.x / (float)screenW) * overlayW);
            int cefY = (int)(((screenH - mouse.y) / (float)screenH) * overlayH);
            if (cefX < 0) cefX = 0;
            else if (cefX >= overlayW) cefX = overlayW - 1;
            if (cefY < 0) cefY = 0;
            else if (cefY >= overlayH) cefY = overlayH - 1;

            // Always forward mouse moves so CEF has correct hover state.
            if (!_hasLastMouse || mouse != _lastMousePosition)
            {
                _reader.WriteInputEvent(
                    Layout.InputMouseMove, Layout.InputBtnNone, cefX, cefY);
                _lastMousePosition = mouse;
                _hasLastMouse = true;
            }

            // Mouse wheel: only forward if pixel is non-transparent.
            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (scroll != 0f && HitTestAlpha(cefX, cefY))
            {
                int delta = (int)(scroll * 120f);
                _reader.WriteInputEvent(
                    Layout.InputMouseWheel, Layout.InputBtnNone, cefX, cefY, delta);
            }

            for (int btn = 0; btn < 3; btn++)
            {
                byte btnCode = UnityButtonToInputBtn(btn);
                if (Input.GetMouseButtonDown(btn))
                {
                    if (HitTestAlpha(cefX, cefY))
                    {
                        _reader.WriteInputEvent(
                            Layout.InputMouseDown, btnCode, cefX, cefY);
                        _cefOwnsClick = true;
                    }
                }
                if (Input.GetMouseButtonUp(btn))
                {
                    if (_cefOwnsClick)
                    {
                        _reader.WriteInputEvent(
                            Layout.InputMouseUp, btnCode, cefX, cefY);
                        _cefOwnsClick = false;
                    }
                }
            }
        }

        /// <summary>
        /// Returns true if the pixel at (cefX, cefY) on the current
        /// canvas IOSurface has alpha > 0. Used to decide whether a
        /// click should go to CEF or pass through to KSP.
        /// </summary>
        private bool HitTestAlpha(int cefX, int cefY)
        {
            if (_currentIoSurfaceId == 0) return false;
            uint bgra;
            if (NativeBridge.DgHudNative_SamplePixel(
                _currentIoSurfaceId, cefX, cefY, out bgra) != 1)
                return false;
            // BGRA: alpha is the high byte.
            byte alpha = (byte)(bgra >> 24);
            return alpha > 0;
        }

        private static byte UnityButtonToInputBtn(int unityButton)
        {
            switch (unityButton)
            {
                case 0: return Layout.InputBtnLeft;
                case 1: return Layout.InputBtnRight;
                case 2: return Layout.InputBtnMiddle;
                default: return Layout.InputBtnNone;
            }
        }

        private void OnDestroy()
        {
            GameEvents.onLevelWasLoadedGUIReady.Remove(OnSceneReady);

            if (_reader != null)
            {
                _reader.Dispose();
                _reader = null;
            }
            if (_overlay != null)
            {
                _overlay.Dispose();
                _overlay = null;
            }
            if (_navBallHider != null)
            {
                _navBallHider.Restore();
                _navBallHider = null;
            }
            Debug.Log(LogPrefix + "addon destroyed");
        }

        /// <summary>
        /// Fallback test pattern when no sidecar is running — diagonal
        /// BGRA gradient + cyan crosshair so we can tell at a glance
        /// whether we're looking at real CEF output (B4) or the
        /// pre-rendered fallback (B3).
        /// </summary>
        private static byte[] BuildTestPattern(int width, int height)
        {
            byte[] buf = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                int yComponent = (y * 255) / (height - 1);
                int row = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int xComponent = (x * 255) / (width - 1);
                    int i = row + x * 4;
                    buf[i + 0] = (byte)xComponent;
                    buf[i + 1] = (byte)yComponent;
                    buf[i + 2] = 255;
                    buf[i + 3] = 160;
                }
            }
            int cx = width / 2;
            int cy = height / 2;
            for (int y = 0; y < height; y++)
            {
                int i = (y * width + cx) * 4;
                buf[i + 0] = 0; buf[i + 1] = 255; buf[i + 2] = 255; buf[i + 3] = 255;
            }
            for (int x = 0; x < width; x++)
            {
                int i = (cy * width + x) * 4;
                buf[i + 0] = 0; buf[i + 1] = 255; buf[i + 2] = 255; buf[i + 3] = 255;
            }
            return buf;
        }
    }
}
