// Intercept the Flight-scene part right-click path. Stock KSP's
// UIPartActionController.SelectPart(Part, bool, bool) is the bottleneck
// that builds its per-part action window and raises
// onPartActionUIShown; we prefix it, fan the part's persistentId out
// to the UI via PawBus (which PawTopic turns into a wire event), and
// return false to veto the stock window.
//
// Scene gate. The patch is a no-op outside Flight. UIPartActionController
// is also used in the SPH/VAB editor for action group binding; letting
// stock run there preserves editor functionality.
//
// Trade-off for MVP. Suppressing the stock PAW unconditionally in
// Flight disables stock-only affordances that live on it — "Run
// Experiment" buttons, Toggle Solar Panel, parachute deploy, etc. —
// because our Dragonglass PAW only renders resources today. Pilots
// still have keyboard action groups + space staging + (soon) our own
// PartModule action surface to fall back on. Revisit if the
// ergonomics cost outranks the feature gain on live play.

using Dragonglass.Telemetry;
using HarmonyLib;

namespace Dragonglass.Hud.Patches
{
    // SelectPart is declared `private` on stock's side (see
    // Assembly-CSharp/UIPartActionController.cs:553), so we reference
    // it by string literal rather than nameof().
    [HarmonyPatch(typeof(UIPartActionController), "SelectPart",
        new[] { typeof(Part), typeof(bool), typeof(bool) })]
    internal static class UIPartActionControllerSelectPartPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Part part)
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT) return true;
            if (part == null) return false;
            PawBus.Raise(part.persistentId);
            return false;
        }
    }
}
