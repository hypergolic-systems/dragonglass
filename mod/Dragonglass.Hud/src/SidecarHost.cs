// Plugin-owned lifecycle for the CEF sidecar process.
//
// Static holder: `SidecarBootstrap.Awake()` calls `EnsureRunning()`
// at KSP startup (Instantly); subsequent calls are no-ops as long as
// the child is still alive. `Application.quitting` kills the child
// on KSP process shutdown so the sidecar doesn't outlive the game.
//
// Binary is at `<dll>/../<sidecar-root>/...` which is where `just
// install` deploys it. On macOS that's `Sidecar/dg-sidecar.app/...`;
// on Windows it's `PluginData/Sidecar/dg-sidecar.exe` — the extra
// PluginData/ hop hides the CEF runtime's .dll files from KSP's
// UrlDir scanner, which otherwise blows up on libcef.dll /
// vulkan-1.dll / etc. whose Win32 FileVersion strings don't parse
// as System.Version. Missing binary is non-fatal: the plugin logs
// a warning and the user can launch the sidecar by hand.
//
// Direct-exec of the Mach-O inside the `.app` bundle (bypassing
// `open`) is what gives us a real PID we can Kill(). The ad-hoc
// codesign baked into `just sidecar-bundle` makes this work without
// Gatekeeper intervention.

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using UnityEngine;
using UDebug = UnityEngine.Debug;

namespace Dragonglass.Hud
{
    public static class SidecarHost
    {
        private const string LogPrefix = "[Dragonglass/Host] ";
        private const int SidecarPort = 9877;

        // Live telemetry WebSocket URL passed to the UI so it
        // auto-attaches on load. Port matches TelemetryAddon's
        // loopback bind in Dragonglass.Telemetry.
        private const string TelemetryWsUrl = "ws://127.0.0.1:8787/";

        // The bare specifier the synthesized shell HTML imports,
        // mapped via the sidecar's import map to a JS module under
        // GameData/<Mod>/UI/. Defaults to stock; mods can replace it
        // via OverrideEntry before the SidecarBootstrap coroutine
        // fires EnsureRunning (i.e. during their own
        // Startup.Instantly Awake — bootstrap yields one frame so
        // overrides registered there land before spawn).
        private static string _entrySpecifier = "@dragonglass/stock";

        /// <summary>
        /// Override the import-map specifier the synthesized shell
        /// imports. Pass a bare specifier like "@nova/hud" or
        /// "@somemod/index". Must be called during Awake of a
        /// Startup.Instantly KSPAddon — sidecar spawn is deferred
        /// one frame past Instantly to give override callers a
        /// chance to register. Last-writer-wins; no priority
        /// semantics. Empty/null inputs are ignored.
        /// </summary>
        public static void OverrideEntry(string specifier)
        {
            if (!string.IsNullOrEmpty(specifier))
            {
                _entrySpecifier = specifier;
            }
        }

        private static readonly object Lock = new object();
        private static Process _proc;
        private static bool _quitHookInstalled;
        private static bool _attempted;

        // Live ShmReader registered by the HUD addon when it opens the
        // SHM file. Static-public callers (NavigateTo, future control
        // ops) reach the producer side of the input ring through this.
        // Cleared on dispose so we don't write through a stale handle.
        private static ShmReader _registeredReader;
        private static bool _navigateNoReaderLogged;

        /// <summary>
        /// Unique session ID for this KSP instance. Used to derive
        /// instance-specific SHM paths so multiple KSP instances
        /// don't stomp each other.
        /// </summary>
        public static readonly string SessionId = System.Guid.NewGuid().ToString("N").Substring(0, 8);

        /// <summary>
        /// Idempotent: spawn the sidecar if we haven't already and no
        /// other process is already listening on the sidecar port.
        /// Safe to call from any addon's Start().
        /// </summary>
        public static void EnsureRunning()
        {
            lock (Lock)
            {
                if (!_quitHookInstalled)
                {
                    Application.quitting += Stop;
                    _quitHookInstalled = true;
                }

                if (_proc != null && !SafeHasExited(_proc))
                {
                    return;
                }

                if (_attempted && _proc == null)
                {
                    // Previous spawn failed in this session; don't
                    // thrash on every Flight-scene entry.
                    return;
                }
                _attempted = true;

                if (PortInUse(SidecarPort))
                {
                    UDebug.Log(LogPrefix + "port " + SidecarPort +
                        " already in use — assuming external sidecar, skipping spawn");
                    return;
                }

                string binary = ResolveBinary();
                if (binary == null)
                {
                    UDebug.LogWarning(LogPrefix +
                        "sidecar binary not found (run just install) — skipping spawn");
                    return;
                }

                string gameDataDir = ResolveGameDataDir();
                if (gameDataDir == null)
                {
                    UDebug.LogWarning(LogPrefix +
                        "GameData root not found — Dragonglass.Hud.dll location unrecognised");
                    return;
                }

                float deviceScale = ResolveDeviceScale();
                UDebug.Log(LogPrefix + "device scale factor → " +
                    deviceScale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));

                try
                {
                    // GameData path may contain spaces on Windows
                    // (e.g. "C:\Users\Alex Rickabaugh\..."), so wrap
                    // it in double quotes. SessionId is hex,
                    // TelemetryWsUrl has no whitespace, --entry is a
                    // bare specifier — none of those need quoting.
                    var psi = new ProcessStartInfo
                    {
                        FileName = binary,
                        Arguments = SessionId + " " + TelemetryWsUrl
                            + " --gamedata=\"" + gameDataDir + "\""
                            + " --entry=" + _entrySpecifier
                            + " --device-scale=" +
                            deviceScale.ToString("0.###",
                                System.Globalization.CultureInfo.InvariantCulture),
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        WorkingDirectory = Path.GetDirectoryName(binary),
                    };

                    _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                    _proc.OutputDataReceived += OnStdout;
                    _proc.ErrorDataReceived += OnStderr;
                    _proc.Exited += OnExited;

                    if (!_proc.Start())
                    {
                        UDebug.LogError(LogPrefix + "Process.Start returned false");
                        _proc = null;
                        return;
                    }
                    _proc.BeginOutputReadLine();
                    _proc.BeginErrorReadLine();

                    UDebug.Log(LogPrefix + "sidecar spawned, pid=" + _proc.Id +
                        " binary=" + binary);
                }
                catch (Exception e)
                {
                    UDebug.LogError(LogPrefix + "spawn failed: " + e);
                    SafeDispose();
                }
            }
        }

        /// <summary>
        /// Tell the sidecar to navigate the CEF main frame to
        /// <paramref name="url"/>. The live telemetry WS URL is
        /// appended as a `ws=<encoded>` query param before dispatch,
        /// matching how the sidecar augments the boot URL — so the UI
        /// reconnects to the live feed across navigations the same
        /// way it does on first load. Routed through the plugin→
        /// sidecar input ring; safe to call from any KSP thread the
        /// addon's Update() runs on. Returns false if the URL was
        /// rejected (empty, too long, ring full) or the sidecar isn't
        /// connected yet.
        /// </summary>
        public static bool NavigateTo(string url)
        {
            ShmReader reader;
            lock (Lock)
            {
                reader = _registeredReader;
            }
            if (reader == null)
            {
                if (!_navigateNoReaderLogged)
                {
                    _navigateNoReaderLogged = true;
                    UDebug.LogWarning(LogPrefix + "NavigateTo dropped — sidecar not connected yet");
                }
                return false;
            }
            _navigateNoReaderLogged = false;
            return reader.WriteNavigate(AppendTelemetryWs(url));
        }

        /// <summary>
        /// Append `ws=<encoded TelemetryWsUrl>` to <paramref name="url"/>
        /// using `?` or `&` depending on whether a query string already
        /// exists, and preserving any `#fragment` at the tail.
        /// </summary>
        private static string AppendTelemetryWs(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            int frag = url.IndexOf('#');
            string head = frag < 0 ? url : url.Substring(0, frag);
            string tail = frag < 0 ? "" : url.Substring(frag);
            string sep = head.IndexOf('?') < 0 ? "?" : "&";
            return head + sep + "ws=" + Uri.EscapeDataString(TelemetryWsUrl) + tail;
        }

        /// <summary>
        /// Called by the HUD addon once it has opened the SHM file.
        /// </summary>
        internal static void RegisterReader(ShmReader reader)
        {
            lock (Lock) { _registeredReader = reader; }
        }

        /// <summary>
        /// Called by the HUD addon before it disposes its ShmReader.
        /// </summary>
        internal static void ClearReader()
        {
            lock (Lock) { _registeredReader = null; }
        }

        /// <summary>
        /// Kill the child process if it's still alive. Called via
        /// `Application.quitting` on KSP shutdown.
        /// </summary>
        public static void Stop()
        {
            lock (Lock)
            {
                if (_proc == null) return;
                try
                {
                    if (!SafeHasExited(_proc))
                    {
                        _proc.Kill();
                        _proc.WaitForExit(2000);
                    }
                    UDebug.Log(LogPrefix + "sidecar stopped");
                }
                catch (Exception e)
                {
                    UDebug.LogWarning(LogPrefix + "stop error: " + e.Message);
                }
                finally
                {
                    SafeDispose();
                }
            }
        }

        private static void SafeDispose()
        {
            try { _proc?.Dispose(); } catch { }
            _proc = null;
        }

        private static bool SafeHasExited(Process p)
        {
            try { return p.HasExited; }
            catch { return true; }
        }

        /// <summary>
        /// Sidecar stdout carries JS console messages from CEF's
        /// DisplayHandler. Route to UDebug.Log so they appear in
        /// KSP.log and the in-game debug console.
        /// </summary>
        private static void OnStdout(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            UDebug.Log("[Dragonglass/HUD/JS] " + e.Data);
        }

        /// <summary>
        /// Sidecar stderr carries operational diagnostics (startup,
        /// frame stats, errors). Route to UDebug.LogWarning so they
        /// stand out in KSP.log.
        /// </summary>
        private static void OnStderr(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            UDebug.LogWarning("[Dragonglass/sidecar] " + e.Data);
        }

        private static void OnExited(object sender, EventArgs e)
        {
            int code = -1;
            try { code = ((Process)sender).ExitCode; } catch { }
            UDebug.LogWarning(LogPrefix + "sidecar exited, code=" + code);
        }

        private static string ResolveBinary()
        {
            // Plugin DLL is at <KSPRoot>/GameData/Dragonglass_Hud/Plugins/Dragonglass.Hud.dll.
            // Sidecar layout (see module header): macOS keeps the .app
            // at <modDir>/Sidecar/ since bundles aren't .dll files KSP
            // scans. Windows puts the flat CEF distribution under
            // <modDir>/PluginData/Sidecar/ so UrlDir skips the subtree.
            try
            {
                string dllPath = Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrEmpty(dllPath)) return null;
                string pluginsDir = Path.GetDirectoryName(dllPath);
                string modDir = Path.GetDirectoryName(pluginsDir);

                string candidate;
                switch (Application.platform)
                {
                    case RuntimePlatform.OSXPlayer:
                    case RuntimePlatform.OSXEditor:
                        candidate = Path.Combine(modDir, "Sidecar",
                            "dg-sidecar.app", "Contents", "MacOS", "dg-sidecar");
                        break;
                    case RuntimePlatform.WindowsPlayer:
                    case RuntimePlatform.WindowsEditor:
                        candidate = Path.Combine(modDir, "PluginData", "Sidecar", "dg-sidecar.exe");
                        break;
                    default:
                        // Linux / other — place the ELF under PluginData/
                        // too once we port; it's the same .so scanner hazard.
                        candidate = Path.Combine(modDir, "PluginData", "Sidecar", "dg-sidecar");
                        break;
                }
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception e)
            {
                UDebug.LogWarning(LogPrefix + "binary resolve failed: " + e.Message);
            }
            return null;
        }

        /// <summary>
        /// Resolve the GameData root the sidecar should serve from.
        /// The sidecar synthesizes the shell HTML at request time and
        /// scans GameData for mod UI directories — Dragonglass_Hud
        /// itself is just one of them, the one whose UI/ contains the
        /// runtime ESM bundles (svelte, stock, instruments, telemetry).
        ///
        /// gameDataDir = parent of <modDir> (i.e. .../GameData/).
        /// Returns null if the resolution fails — usually means the
        /// DLL was loaded from an unexpected location.
        /// </summary>
        private static string ResolveGameDataDir()
        {
            try
            {
                string dllPath = Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrEmpty(dllPath)) return null;
                string pluginsDir = Path.GetDirectoryName(dllPath);
                string modDir = Path.GetDirectoryName(pluginsDir);
                string candidate = Path.GetDirectoryName(modDir);
                if (!Directory.Exists(candidate)) return null;
                return candidate;
            }
            catch (Exception e)
            {
                UDebug.LogWarning(LogPrefix + "GameData resolve failed: " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// Resolve the device scale factor we want CEF to expose as
        /// <c>window.devicePixelRatio</c>. On macOS we prefer the
        /// native plugin's <c>DgHudNative_GetBackingScale</c> (reads
        /// the KSP NSWindow directly). If that returns 0 or throws
        /// (plugin absent on non-macOS until we port it), fall back
        /// to <c>Screen.dpi / 96</c>. Clamped to [0.5, 3.0] so a
        /// broken DPI reading can't produce an unrenderably large
        /// viewport.
        /// </summary>
        private static float ResolveDeviceScale()
        {
            try
            {
                float s = NativeBridge.DgHudNative_GetBackingScale();
                if (s > 0.0f)
                {
                    return Mathf.Clamp(s, 0.5f, 3.0f);
                }
            }
            catch (Exception e)
            {
                // DllNotFound on non-macOS, EntryPointNotFound if an
                // older plugin is deployed. Non-fatal.
                UDebug.Log(LogPrefix + "native backing-scale probe failed (" +
                    e.GetType().Name + "); using Screen.dpi fallback");
            }
            float dpi = Screen.dpi;
            if (dpi > 0f)
            {
                return Mathf.Clamp(dpi / 96f, 0.5f, 3.0f);
            }
            return 1.0f;
        }

        private static bool PortInUse(int port)
        {
            // Try to bind the port ourselves. If it succeeds, nothing
            // else is on it; if it fails with AddressAlreadyInUse, an
            // external sidecar is already listening. Release the probe
            // socket immediately so the real sidecar can bind it.
            try
            {
                using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, port)))
                {
                    return false;
                }
            }
            catch (SocketException)
            {
                return true;
            }
        }
    }
}
