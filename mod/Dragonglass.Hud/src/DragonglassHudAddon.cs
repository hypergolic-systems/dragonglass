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
using System.Collections;
using System.IO;
using System.Reflection;
using HarmonyLib;
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

        // True when <see cref="KeyboardLockId"/> is currently held via
        // InputLockManager. Tracks the sidecar's "CEF wants keyboard"
        // flag so we only call SetControlLock / RemoveControlLock on
        // the edges, not every frame.
        private bool _kbLockActive;

        private float _nextShmRetryTime;
        private const float ShmRetryInterval = 0.5f;

        // True between onGameSceneLoadRequested and
        // onLevelWasLoadedGUIReady. Covers every inter-scene transition,
        // including fast sync ones (e.g. Space Center → Editor) where
        // the LoadingBufferMask shows and hides in the same frame — by
        // the time our Update sees the mask, it's already down again,
        // and the stale CEF blit would leak through otherwise.
        private bool _sceneTransitioning;

        private void Awake()
        {
            // Persist across scene transitions so the HUD is mounted
            // from MainMenu through Flight, Space Center, VAB, etc.
            // The UI app decides per-scene what to render.
            DontDestroyOnLoad(gameObject);

            // Keep Update() ticking when the KSP window loses focus so
            // the zero-copy blit pipeline (which only advances inside
            // Update) doesn't freeze while the user is in another app.
            // Stock KSP behaves the same way; this is a safety net for
            // cases where the setting got cleared.
            Application.runInBackground = true;

            // Install Harmony patches that suppress stock Flight UI
            // elements Dragonglass replaces (navball, MET clock,
            // altimeter, vertical-speed, speed readout, trim gauges).
            // See StockUiHider.cs.
            new Harmony("net.alxandria.dragonglass.hud")
                .PatchAll(Assembly.GetExecutingAssembly());

            GameEvents.onGameSceneLoadRequested.Add(OnSceneLoadRequested);
            GameEvents.onLevelWasLoadedGUIReady.Add(OnSceneGuiReady);
        }

        private void OnSceneLoadRequested(GameScenes scene)
        {
            _sceneTransitioning = true;
            // The event fires synchronously from HighLogic.LoadScene,
            // typically deep inside a UI click handler that will
            // block on SceneManager.LoadScene before control returns.
            // Our next Update() won't run until after the block
            // releases, but Unity's render for *this* frame happens
            // after the block too — with the Canvas still in its
            // pre-click state. Flipping visibility off here, not
            // just in Update, makes sure the stale frame is gone
            // before Frame N's render.
            if (_overlay != null) _overlay.Visible = false;
        }
        private void OnSceneGuiReady(GameScenes scene)
        {
            _sceneTransitioning = false;
            if (ScreenshotsEnabled) StartCoroutine(CaptureSceneScreenshot(scene));
        }

        // Debug aid: when DRAGONGLASS_SCREENSHOTS=1 is set in the
        // environment at KSP launch, writes a PNG of the rendered
        // scene to <KSP>/dragonglass-screenshots/<scene>-<HHmmss>.png
        // a few seconds after every GUI-ready event, plus once from
        // Start() for the initial scene the addon woke up in. Useful
        // for verifying the overlay is actually landing on screen
        // without having to drive the UI interactively.
        private static readonly bool ScreenshotsEnabled =
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DRAGONGLASS_SCREENSHOTS"));

        private IEnumerator CaptureSceneScreenshot(GameScenes scene)
        {
            // Give CEF time to paint the post-transition frame through
            // the blit pipeline: 2 realtime seconds is generous but
            // scene transitions are rare so the cost is negligible.
            // WaitForEndOfFrame makes sure the screenshot grabs a
            // fully composited frame including our ScreenSpaceOverlay.
            yield return new WaitForSecondsRealtime(2f);
            yield return new WaitForEndOfFrame();
            string path = null;
            try
            {
                string dir = Path.Combine(KSPUtil.ApplicationRootPath, "dragonglass-screenshots");
                Directory.CreateDirectory(dir);
                path = Path.Combine(dir,
                    scene + "-" + DateTime.Now.ToString("HHmmss") + ".png");
                InvokeScreenCapture(path);
                Debug.Log(LogPrefix + "scene screenshot queued: " + path);
            }
            catch (Exception e)
            {
                Debug.LogWarning(LogPrefix + "screenshot failed (" +
                    (path ?? "<no path>") + "): " + e.Message);
            }
        }

        // UnityEngine.ScreenCapture lives in UnityEngine.ScreenCaptureModule,
        // which our stubs/ don't ship (keeping the reference set minimal).
        // For a debug-only hook, looking it up reflectively at runtime is
        // fine — KSP's Player ships the real module next to UnityPlayer.dll.
        private static void InvokeScreenCapture(string path)
        {
            Type t = Type.GetType("UnityEngine.ScreenCapture, UnityEngine.ScreenCaptureModule")
                ?? Type.GetType("UnityEngine.ScreenCapture, UnityEngine.CoreModule")
                ?? Type.GetType("UnityEngine.ScreenCapture, UnityEngine");
            if (t == null)
            {
                throw new InvalidOperationException(
                    "UnityEngine.ScreenCapture type not found in loaded assemblies");
            }
            MethodInfo mi = t.GetMethod(
                "CaptureScreenshot",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(string) },
                modifiers: null);
            if (mi == null)
            {
                throw new InvalidOperationException(
                    "ScreenCapture.CaptureScreenshot(string) method not found");
            }
            mi.Invoke(null, new object[] { path });
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

            // onLevelWasLoadedGUIReady already fired for the initial
            // scene before our Awake() subscribed, so capture it here
            // directly if debug screenshots are enabled.
            if (ScreenshotsEnabled) StartCoroutine(CaptureSceneScreenshot(HighLogic.LoadedScene));
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
                SidecarHost.RegisterReader(_reader);
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

                // Register the built-in checkerboard test stream once
                // the GL pipeline is live. Lets a `<PunchThrough id="test">`
                // in the HUD render the checkerboard under the chroma
                // fill before any real portrait capture is wired up.
                try
                {
                    uint h = NativeBridge.DgHudNative_RegisterTestStream(128, 128);
                    Debug.Log(LogPrefix + "punch-through test stream registered (id_hash=0x" +
                        h.ToString("x8") + ")");
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning(LogPrefix + "test stream register failed: " + e.Message);
                }
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
                SampleAndForwardKeyboard();
                UpdateKeyboardLock();
                MaybeEmitResize();
            }

            // --- Gate the overlay. Union of three signals:
            //       • LoadingBufferMask.camera.enabled — the
            //         planet-spinner overlay, up for the visible chunk
            //         of async transitions (SPACECENTER etc.)
            //       • Scene transition window — between
            //         onGameSceneLoadRequested and
            //         onLevelWasLoadedGUIReady, covers sync transitions
            //         (e.g. SPACECENTER → EDITOR) where the mask shows
            //         and hides in the same frame.
            //       • Pause menu open — so the stock ESC dialog reads
            //         against the game scene instead of our HUD.
            //     Hiding the canvas stops Unity compositing the stale
            //     CEF blit over whatever KSP draws in the transition.
            //     We still run the blit pipeline underneath so the
            //     backing texture is current by the time we un-hide. ---
            bool hide = IsLoadingMaskVisible() || _sceneTransitioning || IsPauseMenuOpen();
            if (_overlay != null) _overlay.Visible = !hide;

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
        /// Forwarding policy:
        ///   • Moves and button down/up are forwarded unconditionally.
        ///     CEF benefits from seeing every click regardless of
        ///     alpha — clicks over transparent regions land on nothing
        ///     in the DOM (inert) but let open menus / popovers detect
        ///     "user clicked outside me" and self-dismiss. The job of
        ///     keeping HUD-destined input away from KSP is handled by
        ///     HudRaycastFilter (Unity ICanvasRaycastFilter) on the
        ///     other side — CEF is the always-on recipient.
        ///   • Wheel stays alpha-gated: otherwise a scroll over the
        ///     game world would simultaneously zoom the camera AND
        ///     try to scroll something in CEF, which is confusing
        ///     even when nothing in CEF is actually scrollable.
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

            // Held-button bitmask, included on every non-wheel mouse
            // event so CEF can set MouseEvent.modifiers. Chromium
            // needs these bits during drag — without them, mousemove
            // looks like free hover and text selection never starts.
            int held = 0;
            if (Input.GetMouseButton(0)) held |= Layout.MouseHeldLeft;
            if (Input.GetMouseButton(1)) held |= Layout.MouseHeldRight;
            if (Input.GetMouseButton(2)) held |= Layout.MouseHeldMiddle;

            // Always forward mouse moves so CEF has correct hover state.
            if (!_hasLastMouse || mouse != _lastMousePosition)
            {
                _reader.WriteInputEvent(
                    Layout.InputMouseMove, Layout.InputBtnNone, cefX, cefY, held);
                _lastMousePosition = mouse;
                _hasLastMouse = true;
            }

            // Mouse wheel: alpha-gated so a scroll over the game
            // world only zooms the camera, not both.
            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (scroll != 0f && HitTestAlpha(cefX, cefY))
            {
                int delta = (int)(scroll * 120f);
                _reader.WriteInputEvent(
                    Layout.InputMouseWheel, Layout.InputBtnNone, cefX, cefY, delta);
            }

            // Buttons: unconditional forward. Matches the "CEF is the
            // always-on recipient" policy above. `_cefOwnsClick` is
            // gone — we no longer need to pair alpha-gated downs with
            // ups, since every down forwards.
            for (int btn = 0; btn < 3; btn++)
            {
                byte btnCode = UnityButtonToInputBtn(btn);
                if (Input.GetMouseButtonDown(btn))
                {
                    _reader.WriteInputEvent(
                        Layout.InputMouseDown, btnCode, cefX, cefY, held);
                }
                if (Input.GetMouseButtonUp(btn))
                {
                    _reader.WriteInputEvent(
                        Layout.InputMouseUp, btnCode, cefX, cefY, held);
                }
            }
        }

        // Toggle a `ControlTypes.KEYBOARDINPUT` KSP input lock to match
        // the sidecar's "CEF has an editable focused" flag. Without
        // this, KSP shortcut keys (stage, SAS, RCS, WASD trim, etc.)
        // fire while the user is typing into a web input — the exact
        // reason stock KSP applies the same lock to its own search
        // fields (see `InputLockManager.SetControlLock(ControlTypes.
        // KEYBOARDINPUT, "CraftSearchFieldTextInput")` and siblings
        // in Assembly-CSharp). The flag flips on CEF's
        // `on_virtual_keyboard_requested` callback, which fires on
        // every focus/blur of an editable node.
        private const string KeyboardLockId = "DragonglassCefKeyboard";

        private void UpdateKeyboardLock()
        {
            bool wants = _reader.ReadCefWantsKeyboard();
            if (wants == _kbLockActive) return;

            if (wants)
            {
                InputLockManager.SetControlLock(ControlTypes.KEYBOARDINPUT, KeyboardLockId);
            }
            else
            {
                InputLockManager.RemoveControlLock(KeyboardLockId);
            }
            _kbLockActive = wants;
        }

        /// <summary>
        /// Forward keyboard events to CEF. `OnGUI` is the only Unity
        /// hook that gives us `Event.current.KeyDown` / `KeyUp` with
        /// both a `keyCode` and a resolved `character` — `Update` +
        /// `Input.GetKey` would lose the character mapping and
        /// `Input.inputString` would lose the keyCode. Unity emits
        /// `KeyDown` twice per press: once with just the keyCode, once
        /// with just the character. We forward each independently as
        /// `INPUT_KEY_DOWN` (VK + modifiers) and `INPUT_KEY_CHAR`
        /// (UTF-16 code unit), matching CEF's RAWKEYDOWN → CHAR →
        /// KEYUP contract.
        ///
        /// Forward unconditionally — CEF silently drops key events
        /// when nothing editable is focused, so there's no harm in
        /// always sending. The KSP-side cost of shortcut collisions
        /// is handled by <see cref="UpdateKeyboardLock"/>.
        /// </summary>
        /// <summary>
        /// Poll Unity's input state for keyboard events and forward to
        /// CEF. We tried `OnGUI` + `Event.current.KeyDown / KeyUp` —
        /// it gives both a keyCode and a character in one event and
        /// would be the canonical choice, but in the KSP editor scene
        /// OnGUI simply doesn't fire for key events, so we drop back
        /// to polling. Two passes:
        ///
        ///   1. Walk a fixed list of non-character KeyCodes (arrows,
        ///      Backspace, Delete, Escape, …) and emit KEY_DOWN /
        ///      KEY_UP when `GetKeyDown` / `GetKeyUp` fires. These
        ///      drive Chromium's DOM keydown / keyup and its editor
        ///      commands (deleteContentBackward, MoveLeft, …).
        ///   2. Iterate `Input.inputString`, emitting one KEY_CHAR per
        ///      UTF-16 code unit. Covers text-producing keys (letters,
        ///      digits, symbols, Backspace / Tab / Return — Unity
        ///      surfaces those as `\b` / `\t` / `\r` in inputString).
        ///
        /// Scope note: we intentionally skip character-bearing
        /// KeyCodes (A–Z, digits, punctuation, Shift/Ctrl/Alt/Cmd)
        /// in pass 1 — pass 2 handles the character, and Chromium
        /// doesn't need the RAWKEYDOWN for those to type into an
        /// editable. The list below is limited to keys where the
        /// CHAR path alone isn't enough: navigation, editing, and
        /// shortcut triggers web UIs tend to wire keydown listeners
        /// for.
        /// </summary>
        private static readonly KeyCode[] PolledKeyCodes = new[]
        {
            KeyCode.Backspace,
            KeyCode.Tab,
            KeyCode.Return,
            KeyCode.KeypadEnter,
            KeyCode.Escape,
            KeyCode.PageUp,
            KeyCode.PageDown,
            KeyCode.End,
            KeyCode.Home,
            KeyCode.LeftArrow,
            KeyCode.UpArrow,
            KeyCode.RightArrow,
            KeyCode.DownArrow,
            KeyCode.Insert,
            KeyCode.Delete,
            KeyCode.F1, KeyCode.F2, KeyCode.F3, KeyCode.F4,
            KeyCode.F5, KeyCode.F6, KeyCode.F7, KeyCode.F8,
            KeyCode.F9, KeyCode.F10, KeyCode.F11, KeyCode.F12,
        };

        private void SampleAndForwardKeyboard()
        {
            int mods = CurrentModifierMask();

            for (int i = 0; i < PolledKeyCodes.Length; i++)
            {
                KeyCode k = PolledKeyCodes[i];
                if (Input.GetKeyDown(k))
                {
                    int vk = UnityKeyCodeToWindowsVk(k);
                    if (vk != 0)
                    {
                        _reader.WriteInputEvent(
                            Layout.InputKeyDown, Layout.InputBtnNone, vk, mods);
                    }
                }
                if (Input.GetKeyUp(k))
                {
                    int vk = UnityKeyCodeToWindowsVk(k);
                    if (vk != 0)
                    {
                        _reader.WriteInputEvent(
                            Layout.InputKeyUp, Layout.InputBtnNone, vk, mods);
                    }
                }
            }

            string s = Input.inputString;
            if (!string.IsNullOrEmpty(s))
            {
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    // Skip non-text code units that would confuse CEF:
                    //   • C0 controls < 0x20 — Chromium treats CHAR(\b)
                    //     as a text insert and suppresses the
                    //     deleteBackward editor command that the
                    //     RAWKEYDOWN was meant to trigger. We emit
                    //     KEY_DOWN/UP for Backspace/Tab/Return via
                    //     the PolledKeyCodes pass above; the CHAR
                    //     here is redundant at best, harmful at worst.
                    //   • DEL 0x7F — same reason as \b.
                    //   • macOS Cocoa function-key PUA 0xF700–0xF8FF
                    //     — Unity surfaces arrow keys, F-keys, Home,
                    //     End, etc. as these private-use codepoints
                    //     on macOS, and they're exactly the keys
                    //     whose editor commands the RAWKEYDOWN should
                    //     drive. Forwarding them as CHAR kills the
                    //     caret-movement / delete behaviour.
                    if (c < 0x20) continue;
                    if (c == 0x7F) continue;
                    if (c >= 0xF700 && c <= 0xF8FF) continue;
                    _reader.WriteInputEvent(
                        Layout.InputKeyChar, Layout.InputBtnNone, 0, 0, c);
                }
            }
        }

        private static int CurrentModifierMask()
        {
            int m = 0;
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                m |= Layout.KeyModShift;
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                m |= Layout.KeyModControl;
            if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt))
                m |= Layout.KeyModAlt;
            if (Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand))
                m |= Layout.KeyModMeta;
            return m;
        }

        // Unity KeyCode → Windows virtual-key code. CEF (and Chromium's
        // DOM key-event routing) expects Windows VKs regardless of the
        // host platform. ASCII-ish keys (digits, space, backspace, tab,
        // return, escape) happen to share values with Unity KeyCode;
        // everything else diverges. We cover the keys a web UI cares
        // about (text-editing navigation, arrows, function keys, modifier
        // keys) and return 0 for unmapped codes to suppress the event.
        private static int UnityKeyCodeToWindowsVk(KeyCode k)
        {
            // Letters: Unity lowercase (97..122), Windows uppercase (65..90).
            if (k >= KeyCode.A && k <= KeyCode.Z)
                return (int)k - (int)KeyCode.A + 0x41;
            // Top-row digits 0..9 — identical encoding.
            if (k >= KeyCode.Alpha0 && k <= KeyCode.Alpha9)
                return (int)k - (int)KeyCode.Alpha0 + 0x30;
            // Keypad digits → VK_NUMPAD0..9 (0x60..0x69).
            if (k >= KeyCode.Keypad0 && k <= KeyCode.Keypad9)
                return (int)k - (int)KeyCode.Keypad0 + 0x60;
            // Function keys. Unity F1..F15 (282..296) → VK_F1..F15 (0x70..0x7E).
            if (k >= KeyCode.F1 && k <= KeyCode.F15)
                return (int)k - (int)KeyCode.F1 + 0x70;
            switch (k)
            {
                case KeyCode.Backspace: return 0x08;
                case KeyCode.Tab: return 0x09;
                case KeyCode.Clear: return 0x0C;
                case KeyCode.Return: return 0x0D;
                case KeyCode.KeypadEnter: return 0x0D;
                case KeyCode.Pause: return 0x13;
                case KeyCode.CapsLock: return 0x14;
                case KeyCode.Escape: return 0x1B;
                case KeyCode.Space: return 0x20;
                case KeyCode.PageUp: return 0x21;
                case KeyCode.PageDown: return 0x22;
                case KeyCode.End: return 0x23;
                case KeyCode.Home: return 0x24;
                case KeyCode.LeftArrow: return 0x25;
                case KeyCode.UpArrow: return 0x26;
                case KeyCode.RightArrow: return 0x27;
                case KeyCode.DownArrow: return 0x28;
                case KeyCode.Insert: return 0x2D;
                case KeyCode.Delete: return 0x2E;
                case KeyCode.LeftShift: return 0xA0;
                case KeyCode.RightShift: return 0xA1;
                case KeyCode.LeftControl: return 0xA2;
                case KeyCode.RightControl: return 0xA3;
                case KeyCode.LeftAlt: return 0xA4;
                case KeyCode.RightAlt: return 0xA5;
                case KeyCode.LeftCommand: return 0x5B;
                case KeyCode.RightCommand: return 0x5C;
                case KeyCode.Semicolon: return 0xBA;
                case KeyCode.Equals: return 0xBB;
                case KeyCode.Comma: return 0xBC;
                case KeyCode.Minus: return 0xBD;
                case KeyCode.Period: return 0xBE;
                case KeyCode.Slash: return 0xBF;
                case KeyCode.BackQuote: return 0xC0;
                case KeyCode.LeftBracket: return 0xDB;
                case KeyCode.Backslash: return 0xDC;
                case KeyCode.RightBracket: return 0xDD;
                case KeyCode.Quote: return 0xDE;
                default: return 0;
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

        // Stock KSP's inter-scene loading buffer (planet spinner).
        // LoadingBufferMask.Instance is the persistent singleton the
        // rest of KSP uses to show/hide the overlay; its `camera.enabled`
        // flips true inside OnSceneChange (subscribed to
        // onGameSceneLoadRequested) and false when ShowDuration
        // finishes — which spans the whole visible "loading"
        // experience including the FlightGlobals.ready tail into
        // Flight. Instance is internal so we reach it by reflection
        // once, cached.
        private static readonly FieldInfo _maskInstanceField =
            typeof(LoadingBufferMask).GetField("Instance",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        private static bool IsLoadingMaskVisible()
        {
            LoadingBufferMask mask = _maskInstanceField != null
                ? _maskInstanceField.GetValue(null) as LoadingBufferMask
                : null;
            return mask != null && mask.camera != null && mask.camera.enabled;
        }

        // PauseMenu is a flight-scene singleton. `exists` guards against
        // NRE in non-flight scenes where `fetch` is null; `isOpen`
        // returns `fetch.display` and flips the moment Display() runs.
        private static bool IsPauseMenuOpen()
        {
            return PauseMenu.exists && PauseMenu.isOpen;
        }

        private void OnDestroy()
        {
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneLoadRequested);
            GameEvents.onLevelWasLoadedGUIReady.Remove(OnSceneGuiReady);

            // Drop the KSP keyboard lock if we were holding it —
            // otherwise the lock leaks across scene unload and KSP
            // stays deaf to keyboard input in the next scene.
            if (_kbLockActive)
            {
                InputLockManager.RemoveControlLock(KeyboardLockId);
                _kbLockActive = false;
            }

            if (_reader != null)
            {
                SidecarHost.ClearReader();
                _reader.Dispose();
                _reader = null;
            }
            if (_overlay != null)
            {
                _overlay.Dispose();
                _overlay = null;
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
