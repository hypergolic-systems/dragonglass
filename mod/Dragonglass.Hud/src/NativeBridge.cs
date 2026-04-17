// P/Invoke wrapper for DgHudNative.bundle.
//
// Resolved by Unity's native plugin loader at first DllImport call.
// On macOS the plugin lives at GameData/Dragonglass_Hud/Plugins/DgHudNative.bundle
// as a proper bundle directory, with a flat libDgHudNative.dylib
// sibling for Mono loaders that prefer the plain dylib pattern.
//
// The zero-copy design has Unity own the destination texture:
//
//   1. C# creates a normal Texture2D(width, height, BGRA32).
//   2. C# passes `texture.GetNativeTexturePtr()` to the plugin via
//      `SetTargetTexture`. On OpenGL Core this is a GLuint texture name;
//      on Metal it's an `id<MTLTexture>` pointer.
//   3. Each Update() C# calls `UpdatePending(id, gen)` to publish the
//      latest sidecar IOSurface state, then
//      `GL.IssuePluginEvent(renderEventFunc, 0)` to request a
//      render-thread blit.
//   4. Inside the render event, the plugin wraps the IOSurface as a
//      source texture (CGL on GL, iosurface descriptor on Metal) and
//      blits the contents into Unity's destination texture. No CPU
//      round trip.

using System;
using System.Runtime.InteropServices;

namespace Dragonglass.Hud
{
    internal static class NativeBridge
    {
        private const string Lib = "DgHudNative";

        /// <summary>Returns 1 if the native plugin's Unity load ran
        /// successfully and a supported graphics backend was detected.</summary>
        [DllImport(Lib)]
        public static extern int DgHudNative_IsReady();

        /// <summary>Backend tag: 0 unknown, 1 OpenGL Core, 2 Metal.</summary>
        [DllImport(Lib)]
        public static extern int DgHudNative_GetBackend();

        /// <summary>Tell the plugin which graphics backend Unity is
        /// running. 1=OpenGLCore, 2=Metal. Required because Unity
        /// doesn't invoke UnityPluginLoad on pure DllImport plugins,
        /// so the native side can't query IUnityGraphics directly.</summary>
        [DllImport(Lib)]
        public static extern void DgHudNative_SetBackend(int backend);

        /// <summary>Register the Unity-owned destination texture.
        /// <paramref name="nativeTex"/> is the value returned by
        /// <c>Texture2D.GetNativeTexturePtr()</c>.</summary>
        [DllImport(Lib)]
        public static extern void DgHudNative_SetTargetTexture(
            IntPtr nativeTex, int width, int height);

        /// <summary>Publish the latest (IOSurfaceID, generation) pair.
        /// Cheap atomic store; real work happens inside the render event.</summary>
        [DllImport(Lib)]
        public static extern void DgHudNative_UpdatePending(
            uint ioSurfaceId, uint ioSurfaceGen);

        /// <summary>Fetch the render-event function pointer for
        /// <c>GL.IssuePluginEvent</c>.</summary>
        [DllImport(Lib)]
        public static extern IntPtr DgHudNative_GetRenderEventFunc();

        /// <summary>Diagnostic counters. Useful once per second in the
        /// fps reporter to see whether blits are actually advancing.</summary>
        [DllImport(Lib)]
        public static extern void DgHudNative_GetStats(
            out ulong blits, out ulong errors, out ulong cacheMisses);

        /// <summary>Per-branch error counters so we can pinpoint which
        /// step of the GL blit path is failing when errors are non-zero.</summary>
        [DllImport(Lib)]
        public static extern void DgHudNative_GetErrorBreakdown(
            out ulong noCtx, out ulong iosurfaceLookup,
            out ulong cglTexImage, out ulong fboIncomplete,
            out ulong noDest);

        /// <summary>Read a single BGRA pixel from a globally-lookupable
        /// IOSurface via IOSurfaceLock. Used by the latency probe to
        /// sample the marker rect from the canvas IOSurface — the
        /// shm payload is stale under the zero-copy pipeline.
        /// Returns 1 on success, 0 on failure.</summary>
        [DllImport(Lib)]
        public static extern int DgHudNative_SamplePixel(
            uint ioSurfaceId, int x, int y, out uint outBgra);

        /// <summary>Fetch the last error message the native plugin
        /// recorded via set_last_error. Used after StartSidecar and
        /// other calls return non-zero to get a human-readable
        /// explanation into KSP.log.</summary>
        [DllImport(Lib)]
        public static extern void DgHudNative_GetLastError(
            [Out] System.Text.StringBuilder buf, int bufLen);

        public static string GetLastError()
        {
            var sb = new System.Text.StringBuilder(512);
            try
            {
                DgHudNative_GetLastError(sb, sb.Capacity);
            }
            catch (System.Exception) { }
            return sb.ToString();
        }

        /// <summary>
        /// Probe once at plugin start to decide whether the zero-copy
        /// path is available. Wraps the native call so a missing dylib
        /// turns into a soft "not available" rather than a
        /// DllNotFoundException that crashes Start().
        /// </summary>
        public static bool TryProbe(
            UnityEngine.Rendering.GraphicsDeviceType deviceType,
            out string error, out int backend)
        {
            error = null;
            backend = 0;
            int b = 0;
            switch (deviceType)
            {
                case UnityEngine.Rendering.GraphicsDeviceType.OpenGLCore: b = 1; break;
                case UnityEngine.Rendering.GraphicsDeviceType.Metal:      b = 2; break;
                default:
                    error = "unsupported graphics backend: " + deviceType;
                    return false;
            }
            try
            {
                DgHudNative_SetBackend(b);
                if (DgHudNative_IsReady() != 1)
                {
                    error = "plugin loaded but IsReady=0 after SetBackend(" + b + ")";
                    return false;
                }
                backend = DgHudNative_GetBackend();
                return true;
            }
            catch (DllNotFoundException e)
            {
                error = "native bundle not found: " + e.Message;
                return false;
            }
            catch (EntryPointNotFoundException e)
            {
                error = "native entry point missing: " + e.Message;
                return false;
            }
            catch (Exception e)
            {
                error = "native probe threw " + e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        public static string BackendName(int backend)
        {
            switch (backend)
            {
                case 1: return "OpenGLCore";
                case 2: return "Metal";
                default: return "Unknown(" + backend + ")";
            }
        }
    }
}
