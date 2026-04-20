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
using HarmonyLib;
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
            private static void Postfix(METDisplay __instance) => HideSelf(__instance, "METDisplay");
        }

        [HarmonyPatch(typeof(AltitudeTumbler), "Start")]
        internal static class AltitudeTumblerPatch
        {
            [HarmonyPostfix]
            private static void Postfix(AltitudeTumbler __instance) => HideSelf(__instance, "AltitudeTumbler");
        }

        [HarmonyPatch(typeof(VerticalSpeedGauge), "Start")]
        internal static class VerticalSpeedGaugePatch
        {
            [HarmonyPostfix]
            private static void Postfix(VerticalSpeedGauge __instance) => HideSelf(__instance, "VerticalSpeedGauge");
        }

        [HarmonyPatch(typeof(SpeedDisplay), "Start")]
        internal static class SpeedDisplayPatch
        {
            [HarmonyPostfix]
            private static void Postfix(SpeedDisplay __instance) => HideSelf(__instance, "SpeedDisplay");
        }

        [HarmonyPatch(typeof(LinearControlGauges), "Start")]
        internal static class LinearControlGaugesPatch
        {
            [HarmonyPostfix]
            private static void Postfix(LinearControlGauges __instance) => HideSelf(__instance, "LinearControlGauges");
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
