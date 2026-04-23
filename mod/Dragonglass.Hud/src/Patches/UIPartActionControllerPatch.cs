// Intercept the part right-click path in Flight and the VAB/SPH editor.
// Stock KSP's UIPartActionController.SelectPart(Part, bool, bool) is the
// bottleneck that builds its per-part action window and raises
// onPartActionUIShown; we prefix it, fan the part's persistentId out
// to the UI via PawBus (which PawTopic turns into a wire event), and
// return false to veto the stock window.
//
// Scene gate. Flight + EDITOR (VAB and SPH) are handled; other scenes
// (Main Menu, Space Center, Tracking Station) have no parts to click
// anyway, but the early return keeps us defensive if stock ever reuses
// the same controller path elsewhere.

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
            GameScenes s = HighLogic.LoadedScene;
            if (s != GameScenes.FLIGHT && s != GameScenes.EDITOR) return true;
            if (part == null) return false;
            PawBus.Raise(part.persistentId);
            return false;
        }
    }
}
