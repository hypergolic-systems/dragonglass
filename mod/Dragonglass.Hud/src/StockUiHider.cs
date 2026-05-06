// Harmony patches that hide stock Flight UI elements Dragonglass replaces.
//
// Each patch is a postfix on a target's Start(). Postfixes run
// synchronously inside Start — before Unity renders the frame — so
// SetActive(false) here means the component is never visible for a
// single frame. It also halts the MonoBehaviour's Update/LateUpdate,
// eliminating the per-frame cost of maintaining UI we don't display.
//
// No restore path: KSP rebuilds the Flight UI on every Flight-scene
// entry, and the patches fire on each new instance.

using System.Reflection;
using Dragonglass.Telemetry;
using HarmonyLib;
using KSP.UI.Screens;
using KSP.UI.Screens.Flight;
using UnityEngine;

namespace Dragonglass.Hud
{
    internal static class StockUiHider
    {
        private const string LogPrefix = "[Dragonglass.Hud/uihide] ";

        // The sphere's visible pixels come from a separate camera
        // (NavBallcamera) that renders the 3D mesh to a RenderTexture,
        // which a RawImage in the UI canvas displays. Disabling the
        // NavballFrame GameObject kills the RawImage; we also disable
        // the external camera so it stops rendering into a texture
        // nobody reads.
        [HarmonyPatch(typeof(NavBall), "Start")]
        internal static class NavBallPatch
        {
            [HarmonyPostfix]
            private static void Postfix(NavBall __instance)
            {
                if (!Capabilities.Has(Capabilities.FlightUi)) return;
                if (__instance == null || __instance.navBall == null) return;

                Transform root = __instance.navBall;
                for (Transform t = __instance.navBall; t != null; t = t.parent)
                {
                    if (t.name == "NavballFrame" || t.name == "NavballPanel")
                    {
                        root = t;
                        break;
                    }
                }
                root.gameObject.SetActive(false);

                foreach (var cam in Object.FindObjectsOfType<Camera>())
                {
                    if (cam != null && cam.name.ToLowerInvariant().Contains("navball"))
                    {
                        cam.gameObject.SetActive(false);
                    }
                }

                Debug.Log(LogPrefix + "hid NavBall (root=" + root.name + ")");
            }
        }

        [HarmonyPatch(typeof(METDisplay), "Start")]
        internal static class METDisplayPatch
        {
            [HarmonyPostfix]
            private static void Postfix(METDisplay __instance)
            {
                if (!Capabilities.Has(Capabilities.FlightUi)) return;
                HideSelf(__instance, "METDisplay");
            }
        }

        [HarmonyPatch(typeof(AltitudeTumbler), "Start")]
        internal static class AltitudeTumblerPatch
        {
            [HarmonyPostfix]
            private static void Postfix(AltitudeTumbler __instance)
            {
                if (!Capabilities.Has(Capabilities.FlightUi)) return;
                HideSelf(__instance, "AltitudeTumbler");
            }
        }

        [HarmonyPatch(typeof(VerticalSpeedGauge), "Start")]
        internal static class VerticalSpeedGaugePatch
        {
            [HarmonyPostfix]
            private static void Postfix(VerticalSpeedGauge __instance)
            {
                if (!Capabilities.Has(Capabilities.FlightUi)) return;
                HideSelf(__instance, "VerticalSpeedGauge");
            }
        }

        [HarmonyPatch(typeof(SpeedDisplay), "Start")]
        internal static class SpeedDisplayPatch
        {
            [HarmonyPostfix]
            private static void Postfix(SpeedDisplay __instance)
            {
                if (!Capabilities.Has(Capabilities.FlightUi)) return;
                HideSelf(__instance, "SpeedDisplay");
            }
        }

        [HarmonyPatch(typeof(LinearControlGauges), "Start")]
        internal static class LinearControlGaugesPatch
        {
            [HarmonyPostfix]
            private static void Postfix(LinearControlGauges __instance)
            {
                if (!Capabilities.Has(Capabilities.FlightUi)) return;
                HideSelf(__instance, "LinearControlGauges");
            }
        }

        // Stock Kerbal portrait gallery (`KerbalPortraitGallery`).
        // Dragonglass paints the IVA portraits via chroma-key
        // punch-through inside the HUD, so the stock visible row
        // would be a duplicate. We hide it visually via a
        // CanvasGroup with alpha=0 / blocksRaycasts=false rather
        // than `SetActive(false)` so:
        //   * the gallery's MonoBehaviours keep ticking (so
        //     `Portraits` stays populated for our scrape),
        //   * `RectContainment` keeps reporting the per-portrait
        //     rect as visible (so each `Kerbal.kerbalCam` stays
        //     enabled and `avatarTexture` stays live),
        //   * pointer events fall through to our HUD (so the user
        //     can click the EVA / IVA buttons we render in HTML).
        [HarmonyPatch(typeof(KerbalPortraitGallery), "Awake")]
        internal static class KerbalPortraitGalleryPatch
        {
            [HarmonyPostfix]
            private static void Postfix(KerbalPortraitGallery __instance)
            {
                if (!Capabilities.Has(Capabilities.FlightUi)) return;
                if (__instance == null) return;
                var go = __instance.gameObject;
                var cg = go.GetComponent<CanvasGroup>();
                if (cg == null) cg = go.AddComponent<CanvasGroup>();
                cg.alpha = 0f;
                cg.blocksRaycasts = false;
                cg.interactable = false;
                Debug.Log(LogPrefix + "hid KerbalPortraitGallery (CanvasGroup α=0)");
            }
        }

        // Stock stager. Dragonglass's StagingStack replaces it;
        // leaving stock's visible alongside causes duplicate icons
        // and conflicting drag targets.
        //
        // Stock's visible root is the `mainListAnchor` VerticalLayoutGroup,
        // a private field on StageManager. We hide it in Awake (the
        // only init method StageManager exposes — no Start override)
        // via reflection — deliberately NOT through
        // `ShowHideStageStack(false)`, which also calls
        // `InputLockManager.SetControlLock(STAGING)` and would kill
        // spacebar-to-stage.
        //
        // Scene gate. StageManager is one MonoBehaviour class instantiated
        // in both Flight and Editor scenes, so the patches below fire in
        // both — gate Flight on `flight/ui` and Editor on `editor/staging`
        // so a UI replacing only one scene's chrome leaves the other
        // alone. (Mirror of the Flight/Editor split in
        // Patches/UIPartActionControllerPatch.cs.)
        private static string StageCapForCurrentScene()
        {
            GameScenes s = HighLogic.LoadedScene;
            return s == GameScenes.FLIGHT ? Capabilities.FlightUi
                 : s == GameScenes.EDITOR ? Capabilities.EditorStaging
                 : null;
        }

        [HarmonyPatch(typeof(StageManager), "Awake")]
        internal static class StageManagerPatch
        {
            private static readonly FieldInfo _anchorField =
                AccessTools.Field(typeof(StageManager), "mainListAnchor");

            [HarmonyPostfix]
            private static void Postfix(StageManager __instance)
            {
                string cap = StageCapForCurrentScene();
                if (cap == null || !Capabilities.Has(cap)) return;
                if (__instance == null || _anchorField == null) return;
                var anchor = _anchorField.GetValue(__instance) as MonoBehaviour;
                if (anchor == null || anchor.gameObject == null)
                {
                    Debug.LogWarning(LogPrefix + "StageManager.mainListAnchor not found — stock stager left visible");
                    return;
                }
                anchor.gameObject.SetActive(false);
                Debug.Log(LogPrefix + "hid StageManager.mainListAnchor");
            }
        }

        // `ShowHideStageStack` is the only stock caller that touches
        // `mainListAnchor.gameObject.SetActive`, and it also takes /
        // releases the STAGING input lock as a side effect. Skipping
        // it entirely (return false from a prefix) keeps the anchor
        // permanently inactive AND stops anything from ever locking
        // STAGING. Known stock callers that would otherwise re-show
        // the stager or lock input:
        //   • `ToggleStageStack` — player-facing visibility key
        //   • `FlightUIModeController` — UI-mode change
        //   • `EVAConstructionModeController` — entering/leaving EVA
        //     construction mode
        [HarmonyPatch(typeof(StageManager), nameof(StageManager.ShowHideStageStack))]
        internal static class ShowHideStageStackPatch
        {
            [HarmonyPrefix]
            private static bool Prefix()
            {
                string cap = StageCapForCurrentScene();
                return cap == null || !Capabilities.Has(cap);
            }
        }

        // Several widgets have no dedicated MonoBehaviour we can patch:
        //   • TimeFrame — top-centre cluster: MET clock, TimeWarp buttons,
        //     alarm clock, Telemetry (comms) indicator
        //   • TopFrame/IVACollapseGroup — upper-right lights/gear/brakes/
        //     abort buttons + temperature/atmosphere slide-out
        //   • UIModeFrame/…/UIModeSelector — mode-switch buttons
        //     (flight/docking/map/maneuver)
        //   • TrackingFilters — in-flight vessel-filter drawer toggle
        //
        // Hook into FlightUIModeController.Start — by the time it runs,
        // the whole Flight UI panel tree is instantiated and parented.
        //
        // Full hierarchy paths (not bare names) — GameObject.Find("NAME")
        // returns the first *active* match globally, and at least one
        // duplicate name exists in stock ("IVACollapseGroup" is both a
        // TopFrame child and a TrackingFilters child). Hitting the wrong
        // sibling breaks the tracking-filter UI and cascades into NREs.
        [HarmonyPatch(typeof(FlightUIModeController), "Start")]
        internal static class FlightUIRootsPatch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!Capabilities.Has(Capabilities.FlightUi)) return;
                HideByPath("Flight/TimeFrame");
                HideByPath("Flight/TopFrame/IVACollapseGroup");
                HideByPath("Flight/UIModeFrame/EVACollapseGroup/UIModeSelector");
                HideByPath("Flight/TrackingFilters");
            }
        }

        private static void HideSelf(MonoBehaviour mb, string label)
        {
            if (mb == null) return;
            mb.gameObject.SetActive(false);
            Debug.Log(LogPrefix + "hid " + label);
        }

        private static void HideByPath(string path)
        {
            var go = GameObject.Find(path);
            if (go == null)
            {
                Debug.LogWarning(LogPrefix + "no GameObject at '" + path + "'");
                return;
            }
            go.SetActive(false);
            Debug.Log(LogPrefix + "hid " + path);
        }
    }
}
