// Plugin-owned lifecycle for the CEF sidecar process.
//
// Static holder: `SidecarBootstrap.Awake()` calls `EnsureRunning()`
// at KSP startup (Instantly); subsequent calls are no-ops as long as
// the child is still alive. `Application.quitting` kills the child
// on KSP process shutdown so the sidecar doesn't outlive the game.
//
// Binary is at `<dll>/../Sidecar/dg-sidecar.app/...` which is where
// `just install` deploys it. Missing binary is non-fatal: the
// plugin logs a warning and the user can launch the sidecar by hand.
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

        private static readonly object Lock = new object();
        private static Process _proc;
        private static bool _quitHookInstalled;
        private static bool _attempted;

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

                string bootUrl = ResolveBootUrl();
                if (bootUrl == null)
                {
                    UDebug.LogWarning(LogPrefix +
                        "no UI found — expected UI/Stock/index.html in mod directory");
                    return;
                }

                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = binary,
                        Arguments = bootUrl + " " + SessionId,
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
            // Sibling sidecar lives at <KSPRoot>/GameData/Dragonglass_Hud/Sidecar/dg-sidecar.app/...
            try
            {
                string dllPath = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(dllPath))
                {
                    string pluginsDir = Path.GetDirectoryName(dllPath);
                    string modDir = Path.GetDirectoryName(pluginsDir);
                    string candidate = Path.Combine(modDir,
                        "Sidecar", "dg-sidecar.app", "Contents", "MacOS", "dg-sidecar");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            catch (Exception e)
            {
                UDebug.LogWarning(LogPrefix + "binary resolve failed: " + e.Message);
            }
            return null;
        }

        /// <summary>
        /// Resolve the UI directory the sidecar should load.
        /// Returns the UI/Stock directory relative to the mod install.
        /// </summary>
        private static string ResolveBootUrl()
        {
            try
            {
                string dllPath = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(dllPath))
                {
                    string pluginsDir = Path.GetDirectoryName(dllPath);
                    string modDir = Path.GetDirectoryName(pluginsDir);
                    string uiDir = Path.Combine(modDir, "UI", "Stock");
                    if (Directory.Exists(uiDir))
                    {
                        return uiDir;
                    }
                }
            }
            catch (Exception e)
            {
                UDebug.LogWarning(LogPrefix + "URL resolve failed: " + e.Message);
            }
            return null;
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
