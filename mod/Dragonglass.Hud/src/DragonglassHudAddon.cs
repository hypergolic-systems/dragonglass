// Dragonglass HUD plugin entry point.
//
// Runs in Flight for now. The sidecar is started by SidecarBootstrap
// before this addon loads, so CEF is warmed up by the time we need it.
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
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class DragonglassHudAddon : MonoBehaviour
    {
        private const string LogPrefix = "[Dragonglass/Hud] ";

        // Must match the sidecar's VIEWPORT_WIDTH/HEIGHT constants in
        // sidecar/src/app.rs AND KSP's physical window resolution —
        // otherwise the RawImage stretches and the HUD gets squished.
        // Temporary: hardcoded until we add a runtime handshake so the
        // plugin can adapt to whatever resolution the user launched at.
        private const int OverlayWidth = 1920;
        private const int OverlayHeight = 1200;

        private OverlayCanvas _overlay;
        private ShmReader _reader;

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

        private void Start()
        {
            Debug.Log(LogPrefix + "addon loaded (" + HighLogic.LoadedScene + ")");

            SidecarHost.EnsureRunning();

            try
            {
                _overlay = new OverlayCanvas(OverlayWidth, OverlayHeight);
                // Preload with the B3 test pattern so we get *something*
                // on screen even if the sidecar isn't running.
                _overlay.ApplyBgra(BuildTestPattern(OverlayWidth, OverlayHeight));
                Debug.Log(LogPrefix + "overlay canvas ready (" + OverlayWidth + "x" + OverlayHeight + ")");
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

            // Hide the stock navball sphere now that our overlay is up.
            // Deferred one frame via an invoke so the NavBall singleton
            // has a chance to finish its own Start() — the renderers may
            // not exist yet in the first Flight-scene tick.
            _navBallHider = new NavBallHider();
            Invoke(nameof(HideStockNavBall), 0.5f);
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
                _reader = ShmReader.Open(shmPath);
                if (_reader.Width != OverlayWidth || _reader.Height != OverlayHeight)
                {
                    Debug.LogWarning(LogPrefix + "shm dimensions " + _reader.Width + "x" + _reader.Height +
                        " do not match overlay " + OverlayWidth + "x" + OverlayHeight + "; ignoring");
                    _reader.Dispose();
                    _reader = null;
                    return;
                }
                Debug.Log(LogPrefix + "shm open: " + shmPath);
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
                NativeBridge.DgHudNative_SetTargetTexture(dest, OverlayWidth, OverlayHeight);
                _zeroCopyAvailable = true;
                Debug.Log(LogPrefix + "zero-copy path active [" +
                    NativeBridge.BackendName(_zeroCopyBackend) +
                    "] dest=" + dest.ToString("x") +
                    " renderEvent=" + _renderEventFunc.ToString("x"));
            }
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
            }

            // --- Pixel pipeline: sidecar → KSP overlay ---
            if (_reader != null && _overlay != null)
            {
                if (_zeroCopyAvailable)
                {
                    ShmReader.HeaderSnapshot snap;
                    if (_reader.TryReadHeader(out snap) && snap.IoSurfaceId != 0)
                    {
                        NativeBridge.DgHudNative_UpdatePending(
                            snap.IoSurfaceId, snap.IoSurfaceGen);
                        if (_renderEventFunc != System.IntPtr.Zero)
                        {
                            GL.IssuePluginEvent(_renderEventFunc, 0);
                        }

                        _currentIoSurfaceId = snap.IoSurfaceId;
                        _currentIoSurfaceGen = snap.IoSurfaceGen;

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

            int cefX = (int)((mouse.x / (float)screenW) * OverlayWidth);
            int cefY = (int)(((screenH - mouse.y) / (float)screenH) * OverlayHeight);
            if (cefX < 0) cefX = 0;
            else if (cefX >= OverlayWidth) cefX = OverlayWidth - 1;
            if (cefY < 0) cefY = 0;
            else if (cefY >= OverlayHeight) cefY = OverlayHeight - 1;

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
