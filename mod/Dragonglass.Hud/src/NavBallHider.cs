// Hide the stock KSP navball while the Dragonglass.Hud addon is alive.
//
// We only toggle MeshRenderer.enabled on the sphere's child meshes —
// the MonoBehaviour itself stays active so `NavBall.LateUpdate()` keeps
// maintaining `relativeGymbal`, and anything else in the game that
// queries the NavBall singleton (autopilot aids, mod hooks, etc.)
// continues to work.
//
// GameObject.SetActive(false) would be simpler but would also pause the
// MonoBehaviour, and downstream code that reads the navball would see
// stale values. Toggling renderers is the minimal-blast-radius option.

using System.Collections.Generic;
using KSP.UI.Screens.Flight;
using UnityEngine;
using UnityEngine.UI;

namespace Dragonglass.Hud
{
    internal sealed class NavBallHider
    {
        private const string LogPrefix = "[Dragonglass.Hud/navhide] ";

        private readonly List<(Renderer r, bool wasEnabled)> _cached =
            new List<(Renderer r, bool wasEnabled)>();
        private bool _applied;

        // The sphere's visible pixels come from a *separate* camera
        // (NavBallcamera) that renders the 3D sphere mesh into a
        // RenderTexture, which a RawImage in the UI canvas displays.
        // Disabling MeshRenderer.enabled on the sphere GO doesn't
        // necessarily stop that camera from painting — and even
        // SetActive(false) on the sphere can leave the RenderTexture
        // full of stale pixels.
        //
        // Cleanest broad strokes: walk up to the "NavballFrame" UI
        // panel ancestor and disable every Renderer and every UGUI
        // Graphic under it. That kills the sphere's displayed pixels
        // (via the RawImage), the frame, and all the gauges/buttons
        // that live inside the panel. We'll selectively re-enable the
        // action-group row later if we want to keep it.
        private readonly List<GameObject> _deactivatedGos = new List<GameObject>();
        private readonly List<(Graphic g, bool wasEnabled)> _cachedGraphics =
            new List<(Graphic g, bool wasEnabled)>();
        private readonly List<(Behaviour b, bool wasEnabled)> _cachedCameras =
            new List<(Behaviour b, bool wasEnabled)>();

        public void TryHide()
        {
            if (_applied) return;

            var navBalls = Object.FindObjectsOfType<NavBall>();
            if (navBalls == null || navBalls.Length == 0)
            {
                Debug.LogWarning(LogPrefix + "no NavBall instances found");
                return;
            }

            foreach (var navBall in navBalls)
            {
                var sphereTransform = navBall.navBall;
                if (sphereTransform == null) continue;

                Debug.Log(LogPrefix + "sphere path: " + PathOf(sphereTransform));

                // Walk up to the root panel — usually "NavballFrame".
                // Fall back to the sphere's immediate parent if no
                // matching ancestor is found.
                var root = sphereTransform;
                for (var t = sphereTransform; t != null; t = t.parent)
                {
                    if (t.name == "NavballFrame" || t.name == "NavballPanel")
                    {
                        root = t;
                        break;
                    }
                }
                Debug.Log(LogPrefix + "root: " + PathOf(root));

                // Disable every Renderer (MeshRenderer, SpriteRenderer,
                // SkinnedMeshRenderer, LineRenderer, ...) in the subtree.
                var renderers = root.GetComponentsInChildren<Renderer>(includeInactive: true);
                foreach (var r in renderers)
                {
                    if (r == null || !r.enabled) continue;
                    _cached.Add((r, r.enabled));
                    r.enabled = false;
                }

                // Disable every UGUI Graphic (Image, RawImage, Text).
                // The RawImage displaying the navball's RenderTexture
                // lives here — this is the one that actually paints
                // the sphere's pixels into the UI canvas.
                var graphics = root.GetComponentsInChildren<Graphic>(includeInactive: true);
                foreach (var g in graphics)
                {
                    if (g == null || !g.enabled) continue;
                    _cachedGraphics.Add((g, g.enabled));
                    g.enabled = false;
                }

                // Kill any Camera whose name hints it's a navball
                // render-to-texture rig. Cameras in UI subtrees are
                // exactly how KSP renders the sphere mesh into the
                // Canvas: find + disable them so the RenderTexture
                // stops updating.
                var cameras = root.GetComponentsInChildren<Camera>(includeInactive: true);
                foreach (var cam in cameras)
                {
                    if (cam == null || !cam.enabled) continue;
                    _cachedCameras.Add((cam, cam.enabled));
                    cam.enabled = false;
                    Debug.Log(LogPrefix + "disabled camera: " + PathOf(cam.transform));
                }

                Debug.Log(LogPrefix + "under " + root.name + ": " +
                    renderers.Length + " renderer(s), " +
                    graphics.Length + " graphic(s), " +
                    cameras.Length + " camera(s)");
            }

            // Also look for the stock NavBall render camera globally.
            // KSP's "NavBallcamera" (or similar) lives outside the UI
            // hierarchy in a dedicated camera rig, separate from the
            // UI subtree — name-based lookup catches it.
            foreach (var cam in Object.FindObjectsOfType<Camera>())
            {
                if (cam == null || !cam.enabled) continue;
                var name = cam.name.ToLowerInvariant();
                if (name.Contains("navball"))
                {
                    _cachedCameras.Add((cam, cam.enabled));
                    cam.enabled = false;
                    Debug.Log(LogPrefix + "disabled global camera: " + PathOf(cam.transform));
                }
            }

            _applied = true;
            Debug.Log(LogPrefix + "hid " + _cached.Count + " renderer(s), " +
                _cachedGraphics.Count + " graphic(s), " +
                _cachedCameras.Count + " camera(s)");
        }

        private static string PathOf(Transform t)
        {
            if (t == null) return "<null>";
            var sb = new System.Text.StringBuilder();
            var cur = t;
            while (cur != null)
            {
                if (sb.Length > 0) sb.Insert(0, '/');
                sb.Insert(0, cur.name);
                cur = cur.parent;
            }
            return sb.ToString();
        }

        public void Restore()
        {
            if (!_applied) return;
            foreach (var (r, wasEnabled) in _cached)
            {
                if (r != null) r.enabled = wasEnabled;
            }
            foreach (var (g, wasEnabled) in _cachedGraphics)
            {
                if (g != null) g.enabled = wasEnabled;
            }
            foreach (var (c, wasEnabled) in _cachedCameras)
            {
                if (c != null) c.enabled = wasEnabled;
            }
            foreach (var go in _deactivatedGos)
            {
                if (go != null) go.SetActive(true);
            }
            Debug.Log(LogPrefix + "restored " + _cached.Count + " renderer(s), " +
                _cachedGraphics.Count + " graphic(s), " +
                _cachedCameras.Count + " camera(s)");
            _cached.Clear();
            _cachedGraphics.Clear();
            _cachedCameras.Clear();
            _deactivatedGos.Clear();
            _applied = false;
        }
    }
}
