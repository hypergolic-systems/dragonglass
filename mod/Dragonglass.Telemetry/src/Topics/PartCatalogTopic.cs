// Editor-only parts catalog. Walks PartLoader.LoadedPartsList once on
// OnEnable and emits the whole list on the wire. Parts don't change
// at runtime — they're defined in .cfg files read at KSP boot — so
// there's no Update() sampling loop; a single MarkDirty in OnEnable
// is enough, the broadcaster flushes a snapshot, and the client
// caches for the duration of the editor session.
//
// Wire format (positional):
//   data: [[name, title, category, manufacturer, cost, mass,
//           description, techRequired, tags, iconBase64,
//           bulkheadProfiles], ...]
//
//     name          : stock internal id ("liquidEngine1"). Stable across
//                     game versions; used as the pick op's key when the
//                     user clicks a row.
//     title         : localized display name ("LV-T45 'Swivel' Liquid
//                     Fuel Engine").
//     category      : stock `PartCategories` enum int. -1 = "none" and
//                     is filtered out server-side — those parts are
//                     hidden from the stock panel too.
//     manufacturer  : localized ("Jebediah Kerman's Junkyard and Spaceship
//                     Parts Co."). Empty string when missing.
//     cost          : funds (career mode). Always emitted; UIs can hide
//                     in sandbox.
//     mass          : partPrefab.mass in tonnes. No public field on
//                     AvailablePart; we reach through the prefab Part.
//     description   : localized, multi-sentence. Bytes-heavy (avg ~200
//                     chars × hundreds of parts); client shows on hover
//                     / click rather than inline.
//     techRequired  : career-tree node id ("basicRocketry"). Empty
//                     string for stock/essential parts available at
//                     Tier 0.
//     tags          : localized searchable tags. Supports multi-word
//                     search without hitting the description.
//     iconBase64    : base64-encoded PNG (no data:image/png prefix —
//                     the client adds it). Captured from the live
//                     `iconPrefab` on first WriteData and cached for
//                     the process lifetime. Empty string when capture
//                     failed (bad prefab, missing layer). Each icon
//                     is ~2-5 KB; the catalog payload is ~1 MB for
//                     stock KSP, cheap for a one-shot emission.
//     bulkheadProfiles : comma-separated stock attach-node profile
//                     ids ("size0", "size1", "mk2", "srf", …). Used
//                     by the client to filter the list by the
//                     player's current attachment context — a size-2
//                     selected node narrows to size-2-compatible
//                     parts. Empty for parts with no stack nodes.

using System.Collections.Generic;
using System.Text;
using Dragonglass.Telemetry.Util;
using UnityEngine;

namespace Dragonglass.Telemetry.Topics
{
    public sealed class PartCatalogTopic : Topic
    {
        public override string Name { get { return "partCatalog"; } }

        protected override void OnEnable()
        {
            base.OnEnable();
            // Parts are static after PartLoader finishes; nothing to
            // sample per frame. Dirty once and let the broadcaster
            // flush a snapshot.
            MarkDirty();
        }

        // Inbound ops. The client fires `pickPart(name)` when the
        // user clicks a row in the catalog panel — we look the part
        // up in the loader and hand it to `EditorLogic.SpawnPart`,
        // which does the heavy lifting (instantiate prefab, attach
        // to cursor, kick the editor FSM into the on_partCreated
        // state). Stock's own placement code takes over from here:
        // the user moves the mouse into the 3D viewport and clicks
        // to drop the part onto an attach node.
        public override void HandleOp(string op, List<object> args)
        {
            switch (op)
            {
                case "pickPart":
                    if (HighLogic.LoadedScene != GameScenes.EDITOR) return;
                    if (args == null || args.Count < 1) return;
                    if (!(args[0] is string name) || string.IsNullOrEmpty(name)) return;
                    AvailablePart ap = PartLoader.getPartInfoByName(name);
                    if (ap == null)
                    {
                        Debug.LogWarning(LogPrefix + "pickPart: unknown part '" + name + "'");
                        return;
                    }
                    if (EditorLogic.fetch == null)
                    {
                        Debug.LogWarning(LogPrefix + "pickPart: EditorLogic.fetch is null");
                        return;
                    }
                    EditorLogic.fetch.SpawnPart(ap);
                    break;
                case "deleteHeld":
                    // UI's "drop on catalog = discard held part" gesture.
                    // Mirrors stock's behaviour when the player clicks
                    // the parts bin while a part is on the cursor:
                    // DestroySelectedPart detaches the part and every
                    // symmetry copy, fires PartDeleted events, and nulls
                    // selectedPart. Idempotent when nothing is held.
                    // `SelectedPart` is a static convenience; the
                    // instance method `DestroySelectedPart` still hangs
                    // off `EditorLogic.fetch`.
                    if (HighLogic.LoadedScene != GameScenes.EDITOR) return;
                    if (EditorLogic.fetch == null) return;
                    if (EditorLogic.SelectedPart == null) return;
                    EditorLogic.fetch.DestroySelectedPart();
                    break;
                default:
                    Debug.LogWarning(LogPrefix + "PartCatalogTopic: unknown op '" + op + "'");
                    break;
            }
        }

        private const string LogPrefix = "[Dragonglass/Telemetry] ";

        public override void WriteData(StringBuilder sb)
        {
            sb.Append('[');
            List<AvailablePart> parts = PartLoader.LoadedPartsList;
            bool first = true;
            if (parts != null)
            {
                for (int i = 0; i < parts.Count; i++)
                {
                    AvailablePart p = parts[i];
                    if (p == null) continue;
                    if (p.partPrefab == null) continue;
                    // PartCategories.none (-1) matches stock's
                    // "hide from editor" marker — debug parts,
                    // EVA-spawned helpers, etc.
                    if ((int)p.category < 0) continue;
                    if (!first) sb.Append(',');
                    first = false;

                    sb.Append('[');
                    Json.WriteString(sb, p.name ?? "");
                    sb.Append(',');
                    Json.WriteString(sb, p.title ?? p.name ?? "");
                    sb.Append(',');
                    Json.WriteLong(sb, (int)p.category);
                    sb.Append(',');
                    Json.WriteString(sb, p.manufacturer ?? "");
                    sb.Append(',');
                    Json.WriteFloat(sb, p.cost);
                    sb.Append(',');
                    Json.WriteFloat(sb, p.partPrefab.mass);
                    sb.Append(',');
                    Json.WriteString(sb, p.description ?? "");
                    sb.Append(',');
                    Json.WriteString(sb, p.TechRequired ?? "");
                    sb.Append(',');
                    Json.WriteString(sb, p.tags ?? "");
                    sb.Append(',');
                    Json.WriteString(sb, PartIconCapture.GetOrCapture(p));
                    sb.Append(',');
                    Json.WriteString(sb, p.bulkheadProfiles ?? "");
                    sb.Append(']');
                }
            }
            sb.Append(']');
        }
    }
}
