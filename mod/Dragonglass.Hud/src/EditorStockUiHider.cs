// Harmony patches that hide stock VAB/SPH editor UI elements
// Dragonglass replaces. Sibling to StockUiHider (Flight-scene) but
// kept separate because the targets + risks are scene-specific:
// in Flight we hide instruments, in the editor we hide parts panels
// and (eventually) staging.
//
// Postfix-on-Start pattern: Harmony runs the postfix synchronously
// right after stock's Start, before the first Unity frame is
// rendered. SetActive(false) at that point means the panel is never
// visible, and its Update loop stops — no CPU cost for a UI we don't
// display.

using HarmonyLib;
using KSP.UI;
using KSP.UI.Screens;
using UnityEngine;

namespace Dragonglass.Hud
{
    internal static class EditorStockUiHider
    {
        private const string LogPrefix = "[Dragonglass.Hud/uihide] ";

        // `EditorPanels` owns the root GameObjects for the editor's
        // left-side UI tree — the parts catalog, category tabs,
        // search field, and the mode-switch buttons (Parts / Actions /
        // Crew / Cargo). Dragonglass paints its own catalog panel in
        // the same screen real estate, so letting stock render its
        // version alongside stacks controls and doubles the drop
        // targets.
        //
        // `partsEditor` is a UIPanelTransition whose gameObject is
        // the whole parts-panel container (list + filters + search).
        // The mode switchers (`partsEditorModes`) sit above it with
        // the Parts/Actions/Crew/Cargo buttons; hiding that too
        // removes the bottom-left cluster whose only function was
        // switching between panels we no longer show.
        [HarmonyPatch(typeof(EditorPanels), "Awake")]
        internal static class EditorPanelsPatch
        {
            [HarmonyPostfix]
            private static void Postfix(EditorPanels __instance)
            {
                if (__instance == null) return;
                HideTransition(__instance.partsEditor, "partsEditor");
                HideTransition(__instance.partsEditorModes, "partsEditorModes");
                HideTransition(__instance.partcategorizerModes, "partcategorizerModes");
                HideTransition(__instance.searchField, "searchField");
            }

            private static void HideTransition(UIPanelTransition t, string label)
            {
                if (t == null || t.gameObject == null)
                {
                    Debug.LogWarning(LogPrefix + "EditorPanels." + label + " not wired — left visible");
                    return;
                }
                t.gameObject.SetActive(false);
                Debug.Log(LogPrefix + "hid EditorPanels." + label);
            }
        }
    }
}
