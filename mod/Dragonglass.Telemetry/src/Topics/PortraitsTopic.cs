// Active Kerbal portrait roster for the chroma-key punch-through HUD.
//
// The mod-side `PortraitCapture` addon registers a stream id like
// `kerbal:Jeb` for each portrait `KerbalPortraitGallery.Instance`
// currently has live. This topic lets the UI know which streams are
// available so it can mount a `<PunchThrough id="kerbal:…">` per
// active crew member without the UI having to reach into KSP itself.
//
// Sampled once per Update; only flushes when the active set actually
// changes (vessel switch, EVA, transfer, death) — quiet outside of
// crew transitions.
//
// Wire format (positional array):
//   data: [
//     [
//       [id, name, role, level],
//       ...
//     ]
//   ]
//
//     id    : stream identifier ("kerbal:Jeb") — same id the
//             native plugin's stream registry is keyed on
//     name  : display name ("Jebediah Kerman")
//     role  : experience trait ("Pilot", "Engineer", "Scientist")
//     level : 0–5 experience level

using System.Collections.Generic;
using System.Text;
using Dragonglass.Telemetry.Util;
using KSP.UI.Screens.Flight;

namespace Dragonglass.Telemetry.Topics
{
    public sealed class PortraitsTopic : Topic
    {
        public override string Name { get { return "portraits"; } }

        public struct Entry
        {
            public string Id;
            public string Name;
            public string Role;
            public int Level;
        }

        private readonly List<Entry> _entries = new List<Entry>();
        // Scratch buffer reused each tick so we can compare the new
        // roster to the old one before deciding to MarkDirty.
        private readonly List<Entry> _scratch = new List<Entry>();

        private void Update()
        {
            _scratch.Clear();
            KerbalPortraitGallery gallery = KerbalPortraitGallery.Instance;
            if (gallery != null && gallery.Portraits != null)
            {
                for (int i = 0; i < gallery.Portraits.Count; i++)
                {
                    KerbalPortrait portrait = gallery.Portraits[i];
                    if (portrait == null) continue;
                    Kerbal kerbal = portrait.crewMember;
                    if (kerbal == null || kerbal.avatarTexture == null) continue;
                    ProtoCrewMember pcm = portrait.crewPcm;
                    if (pcm == null || string.IsNullOrEmpty(pcm.name)) continue;
                    _scratch.Add(new Entry
                    {
                        Id = "kerbal:" + pcm.name,
                        Name = pcm.name,
                        Role = pcm.experienceTrait != null ? pcm.experienceTrait.TypeName : "",
                        Level = pcm.experienceLevel,
                    });
                }
            }

            if (!RostersEqual(_entries, _scratch))
            {
                _entries.Clear();
                _entries.AddRange(_scratch);
                MarkDirty();
            }
        }

        private static bool RostersEqual(List<Entry> a, List<Entry> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                Entry x = a[i];
                Entry y = b[i];
                if (x.Id != y.Id || x.Name != y.Name || x.Role != y.Role || x.Level != y.Level)
                    return false;
            }
            return true;
        }

        public override void WriteData(StringBuilder sb)
        {
            sb.Append('[');
            sb.Append('[');
            for (int i = 0; i < _entries.Count; i++)
            {
                if (i > 0) sb.Append(',');
                Entry e = _entries[i];
                sb.Append('[');
                Json.WriteString(sb, e.Id);
                sb.Append(',');
                Json.WriteString(sb, e.Name);
                sb.Append(',');
                Json.WriteString(sb, e.Role);
                sb.Append(',');
                Json.WriteLong(sb, e.Level);
                sb.Append(']');
            }
            sb.Append(']');
            sb.Append(']');
        }
    }
}
