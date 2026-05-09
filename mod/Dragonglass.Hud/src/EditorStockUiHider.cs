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

        // ApplicationLauncher is the persistent app-button strip stock
        // KSP shows along the screen edge — Engineer's Report, Stock dV,
        // Alarm Clock, KSPedia, Action Groups, Crew Manifest, etc. In
        // the editor it lives along the bottom; UIs that draft their
        // own analysis tools (Δv, mass, crew rosters) want it gone so
        // those affordances don't double up.
        //
        // ApplicationLauncher is `DontDestroyOnLoad`, so a one-shot
        // `Hide()` would persist into Flight too — unwanted, since
        // Flight may still need stock app buttons. We scope by patching
        // `Show()` to short-circuit while we're in the editor scene
        // with `editor/toolbar` declared, and additionally calling
        // `Hide()` once on each EditorAny scene entry to wipe whatever
        // visibility state stock left behind from the previous scene.
        // OnDestroy of the addon (scene exit) calls `Show()` so Flight
        // and the Space Center see the launcher again.
        [HarmonyPatch(typeof(ApplicationLauncher), nameof(ApplicationLauncher.Show))]
        internal static class ApplicationLauncherShowPatch
        {
            [HarmonyPrefix]
            private static bool Prefix()
            {
                if (HighLogic.LoadedScene != GameScenes.EDITOR) return true;
                return !Capabilities.Has(Capabilities.EditorToolbar);
            }
        }

        // Editor-scene addon. Calls Hide() once on entry (after stock
        // initialization has run) and Show() on exit so Flight isn't
        // poisoned. Capabilities arrive via the UI's setCapabilities
        // op, which runs on the same main thread as Start — but the
        // UI may not be connected yet on the very first editor entry
        // after KSP launch. The poll-once-then-stop pattern handles
        // both cases without paying a per-frame cost: we re-check
        // every Update for ~3 s, then disable Update; if caps still
        // haven't arrived by then, the stock launcher stays visible
        // until the next scene transition.
        [KSPAddon(KSPAddon.Startup.EditorAny, once: false)]
        internal class ApplicationLauncherEditorHider : MonoBehaviour
        {
            private const float PollWindow = 3.0f;
            private float _started;
            private bool _applied;

            private void Start()
            {
                _started = Time.realtimeSinceStartup;
                TryApply();
            }

            private void Update()
            {
                if (_applied) { enabled = false; return; }
                if (Time.realtimeSinceStartup - _started > PollWindow)
                {
                    enabled = false;
                    return;
                }
                TryApply();
            }

            private void TryApply()
            {
                if (!Capabilities.Has(Capabilities.EditorToolbar)) return;
                var launcher = ApplicationLauncher.Instance;
                if (launcher == null) return;
                launcher.Hide();
                _applied = true;
                Debug.Log(LogPrefix + "hid ApplicationLauncher (editor/toolbar)");
            }

            private void OnDestroy()
            {
                var launcher = ApplicationLauncher.Instance;
                if (launcher != null) launcher.Show();
            }
        }
    }
}
