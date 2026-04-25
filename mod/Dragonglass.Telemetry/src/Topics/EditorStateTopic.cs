// Minimal editor-scene state the UI needs to reason about user intent
// inside the VAB/SPH. Today the only field is `heldPart` — the stock
// internal name of whatever part is currently attached to the cursor
// (equivalent to `EditorLogic.SelectedPart`), or null when the cursor
// is empty. The catalog panel uses this to flip its click semantics:
// when a part is in hand, clicking anywhere on the panel discards it
// back to stock (mirrors KSP's own "drop on the parts bin" gesture).
//
// Always-on addon: sampling is cheap (one property read) and the
// server emits a broadcast only when the value actually changes via
// MarkDirty, so an empty editor or a non-editor scene costs nothing.
// Keeping it as a permanent topic also means snapshot-on-connect
// replay works across scene transitions — a UI that connects
// mid-editor sees the current held state immediately.
//
// Wire format (positional array):
//   data: [heldPart]
//     heldPart : stock internal part name ("liquidEngine1") or null

using System.Text;
using Dragonglass.Telemetry.Util;
using UnityEngine;

namespace Dragonglass.Telemetry.Topics
{
    public sealed class EditorStateTopic : Topic
    {
        public override string Name { get { return "editorState"; } }

        private string _heldPart;
        public string HeldPart
        {
            get { return _heldPart; }
            set { if (_heldPart != value) { _heldPart = value; MarkDirty(); } }
        }

        private void Update()
        {
            if (HighLogic.LoadedScene != GameScenes.EDITOR || EditorLogic.fetch == null)
            {
                HeldPart = null;
                return;
            }
            Part sel = EditorLogic.SelectedPart;
            HeldPart = sel != null && sel.partInfo != null ? sel.partInfo.name : null;
        }

        public override void WriteData(StringBuilder sb)
        {
            sb.Append('[');
            if (_heldPart == null) Json.WriteNull(sb);
            else Json.WriteString(sb, _heldPart);
            sb.Append(']');
        }
    }
}
