// Editor-only parts catalog. Walks PartLoader.LoadedPartsList once on
// OnEnable and emits the whole list on the wire. Parts don't change
// at runtime — they're defined in .cfg files read at KSP boot — so
// there's no Update() sampling loop; a single MarkDirty in OnEnable
// is enough, the broadcaster flushes a snapshot, and the client
// caches for the duration of the editor session.
//
// Wire format (positional):
//   data: [[name, title, category, manufacturer, cost, mass,
//           description, techRequired, tags], ...]
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
//
// Icons are omitted from the spike — stock uses Unity prefab-rendered
// thumbnails that require a camera capture to produce a bitmap.
// Follow-up task: snapshot each `partPrefab.iconPrefab` into a
// texture and ship a Mime64 PNG or a sprite sheet.

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
                    sb.Append(']');
                }
            }
            sb.Append(']');
        }
    }
}
