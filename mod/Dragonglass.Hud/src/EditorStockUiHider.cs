// Harmony patches that hide stock VAB/SPH editor UI elements
// Dragonglass replaces. Sibling to StockUiHider (Flight-scene) but
// kept separate because the targets + risks are scene-specific.
// Editor staging is gated on `editor/staging` in StockUiHider's
// StageManager patches — same patch target as Flight, so it lives
// next to its Flight twin rather than here.
//
// Deferred-SetActive pattern: we postfix EditorPanels.Awake but
// defer SetActive(false) by one frame. InventoryPanelController
// lives under partsEditor and hasn't had its Awake yet when our
// postfix fires — deactivating immediately prevents its Awake from
// running, leaving the stock singleton InventoryPanelController.Instance
// null. UIPartActionControllerInventory.UpdateCursorOverPAWs reads
// that Instance every frame without a null-check, NREing continuously.

using System.Collections;
using Dragonglass.Telemetry;
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
                if (!Capabilities.Has(Capabilities.EditorParts)) return;
                if (__instance == null) return;
                __instance.StartCoroutine(HideAfterChildAwakes(__instance));
            }

            private static IEnumerator HideAfterChildAwakes(EditorPanels panels)
            {
                yield return null;
                if (panels == null) yield break;
                HideTransition(panels.partsEditor, "partsEditor");
                HideTransition(panels.partsEditorModes, "partsEditorModes");
                HideTransition(panels.partcategorizerModes, "partcategorizerModes");
                HideTransition(panels.searchField, "searchField");
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
