// One-shot rendering of AvailablePart.iconPrefab into a base64 PNG
// for the Dragonglass parts catalog. Stock KSP renders the same
// prefabs live via a dedicated PartListTooltipMasterController camera
// in the 2D UI layer; we don't ship those live frames over the wire
// (that'd saturate a 10 Hz WebSocket), so we capture one snapshot
// per part at editor-scene boot and cache it.
//
// Tradeoff vs stock: stock's icons rotate continuously. Ours are
// still frames. If the staticness reads as lifeless, a follow-up can
// capture a short sprite-sheet (6–8 rotations) per part and cycle
// client-side — same render path, more captures.
//
// Camera setup mirrors PartListTooltipMasterController's config
// (ksp-reference/Assembly-CSharp/KSP.UI.Screens.Editor/PartListTooltipMasterController.cs:78):
//   - Orthographic, size 34
//   - Position (0, -2000, -300), looking +Z
//   - Culls to the "UIAdditional" layer (the same one stock paints
//     editor icons into)
//   - FarClipPlane 295
// Keeping the numbers identical means our captures match what the
// player sees in stock pixel-for-pixel.

using System.Collections.Generic;
using UnityEngine;

namespace Dragonglass.Telemetry.Topics
{
    internal static class PartIconCapture
    {
        private const string LogPrefix = "[Dragonglass/Telemetry] ";
        // 64 square keeps a stock-sized catalog (~300 parts) under a
        // megabyte on the wire in base64 PNG. Large enough to read at
        // the catalog's 48px row height; small enough that PNG
        // compression can lean on flat alpha.
        private const int IconSize = 64;
        private const int IconDepth = 24;

        // Camera parameters mirror PartListTooltipMasterController:
        // orthographic size 34, positioned at Z=-300, far clip 295.
        // Combined, visible Z range in world space is [-299.9, -5]
        // along the camera's forward axis.
        private const float CamOrthoSize = 34f;
        private static readonly Vector3 CamPosition = new Vector3(0f, -2000f, -300f);
        private const float CamFarClip = 295f;
        // PartListTooltipController parents the icon under the camera
        // at localPosition (0, 0, 50) and localScale 50. That puts the
        // mesh 50 world units in front of the camera — comfortably
        // inside the far clip plane — and at the scale stock's live
        // tooltip uses. We mirror both: an icon planted at world Z=0
        // while the camera sits at Z=-300 is 300 units away, beyond
        // the 295 far plane, so every render returns an empty frame
        // and every base64 PNG comes out identical.
        private const float IconScale = 50f;
        private const float IconLocalZ = 50f;

        // In-process cache, keyed by part name. PartLoader completes
        // before the editor scene enters, so we capture lazily on
        // first WriteData and keep the base64 strings for the
        // process lifetime.
        private static readonly Dictionary<string, string> _cache = new Dictionary<string, string>();
        private static Camera _cam;
        private static RenderTexture _rt;
        private static Texture2D _readback;
        private static int _layerId = -1;

        public static string GetOrCapture(AvailablePart ap)
        {
            if (ap == null || ap.iconPrefab == null) return "";
            if (_cache.TryGetValue(ap.name, out string cached)) return cached;

            string b64 = Capture(ap);
            _cache[ap.name] = b64 ?? "";
            return b64 ?? "";
        }

        private static string Capture(AvailablePart ap)
        {
            if (!EnsureCamera()) return null;

            GameObject iconGo = null;
            try
            {
                iconGo = Object.Instantiate(ap.iconPrefab);
                SetLayerRecursive(iconGo, _layerId);

                // Parent under the camera so the icon sits at a known
                // local +Z in front of it, in a camera-relative frame.
                // Matches PartListTooltipController's setup line-for-line
                // and avoids the magic world-Z offset that bit us before.
                iconGo.transform.SetParent(_cam.transform, worldPositionStays: false);
                iconGo.transform.localPosition = new Vector3(0f, 0f, IconLocalZ);
                iconGo.transform.localScale = Vector3.one * IconScale;

                // Stock's 3/4 view: world pitch -15° then a local yaw
                // of -30° around the tilted Y axis. The quaternion
                // product below is equivalent to `rotation = Euler(-15,0,0)`
                // followed by `Rotate(0,-30,0)` in Space.Self.
                iconGo.transform.rotation =
                    Quaternion.Euler(-15f, 0f, 0f) * Quaternion.Euler(0f, -30f, 0f);

                // SetActive last so any Awake/OnEnable on the prefab's
                // MonoBehaviours fires with the transform already in
                // its render pose — same order as stock's tooltip.
                iconGo.SetActive(true);

                RenderTexture prev = RenderTexture.active;
                _cam.Render();
                RenderTexture.active = _rt;
                _readback.ReadPixels(new Rect(0, 0, IconSize, IconSize), 0, 0);
                _readback.Apply();
                RenderTexture.active = prev;

                byte[] png = ImageConversion.EncodeToPNG(_readback);
                return png != null ? System.Convert.ToBase64String(png) : null;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning(LogPrefix + "icon capture failed for '" + ap.name + "': " + e.Message);
                return null;
            }
            finally
            {
                // DestroyImmediate, not Destroy: the whole catalog capture
                // runs inside a single WriteData call on one Unity frame,
                // so deferred destroys would keep every prior clone alive
                // and parented under the camera until end-of-frame. With
                // all clones pinned to the same localPose, they overlap
                // at equal Z and the camera's next Render picks a winner
                // from the pileup by submission order — later captures
                // end up byte-identical to some earlier part's render.
                // Synchronous destroy keeps the hierarchy clean between
                // iterations. Safe here because the clone is private and
                // has no external refs or physics callbacks.
                if (iconGo != null) Object.DestroyImmediate(iconGo);
            }
        }

        private static bool EnsureCamera()
        {
            if (_cam != null) return true;

            // Stock uses the UIAdditional layer for editor icons.
            // If the layer is missing (very old KSP build?), fall back
            // to layer 5 (UI) — won't look right but avoids cull-mask
            // returning a black frame.
            _layerId = LayerMask.NameToLayer("UIAdditional");
            if (_layerId < 0) _layerId = LayerMask.NameToLayer("UI");
            if (_layerId < 0)
            {
                Debug.LogWarning(LogPrefix + "no UIAdditional/UI layer; icon capture disabled");
                return false;
            }

            _rt = new RenderTexture(IconSize, IconSize, IconDepth, RenderTextureFormat.ARGB32);
            _rt.Create();
            _readback = new Texture2D(IconSize, IconSize, TextureFormat.ARGB32, false);

            GameObject go = new GameObject("Dragonglass.IconCaptureCam");
            Object.DontDestroyOnLoad(go);
            _cam = go.AddComponent<Camera>();
            _cam.orthographic = true;
            _cam.orthographicSize = CamOrthoSize;
            _cam.cullingMask = 1 << _layerId;
            _cam.clearFlags = CameraClearFlags.Color;
            // Transparent background — PNG alpha carries through so
            // the UI can compose the icon over its own panel fill.
            _cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            _cam.transform.position = CamPosition;
            _cam.transform.rotation = Quaternion.identity;
            _cam.nearClipPlane = 0.1f;
            _cam.farClipPlane = CamFarClip;
            _cam.enabled = false;  // manual Render() only
            _cam.targetTexture = _rt;
            _cam.allowHDR = false;

            return true;
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            for (int i = 0; i < go.transform.childCount; i++)
            {
                SetLayerRecursive(go.transform.GetChild(i).gameObject, layer);
            }
        }
    }
}
