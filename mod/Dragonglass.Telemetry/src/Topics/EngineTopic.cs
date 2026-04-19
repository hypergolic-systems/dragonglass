// Per-engine telemetry for the active vessel. Sent as a flat array
// of engine "points" with body-local XZ coordinates and a status
// byte; the client renders a SpaceX-livestream-style bottom-up
// engine map from this.
//
// Why a separate topic from FlightTopic:
//   - Lifecycle is different: engine positions are stable within a
//     rigid vessel and only change on staging / docking, while
//     FlightTopic data turns over every frame. Splitting keeps the
//     hot frame path lean.
//   - Dirty cadence is different: we explicitly dead-zone small
//     position jitter (< 1 cm) so the payload only broadcasts on
//     material layout changes or status transitions, not every
//     physics tick.
//
// Coordinate convention. Each engine is projected into vessel body
// frame via `ReferenceTransform.InverseTransformDirection`, then
// we keep (x, z) — the plane perpendicular to the up-stack axis
// (+Y). That yields a bottom-up orthographic view, which is the
// natural "engine map" orientation. Units: meters.
//
// Status byte:
//   0 = burning   (ignited, producing non-zero thrust)
//   1 = flameout  (ignited but starved of propellant)
//   2 = failed    (involuntarily off — best-effort; stock KSP has
//                  no first-class damage state, so we infer)
//   3 = shutdown  (off because it hasn't been staged yet / idle)
//
// Wire format (positional array):
//   data: [vesselId, [ [id, mapX, mapY, status, maxThrust], ... ]]
//
//     maxThrust : configured maximum thrust at standard conditions
//                 (vacuum for most engines), kN. Stable across flight
//                 — the client uses it to scale circle radius so the
//                 engine map reads as a bubble chart where area
//                 encodes thrust magnitude.

using System.Collections.Generic;
using System.Text;
using Dragonglass.Telemetry.Util;
using UnityEngine;

namespace Dragonglass.Telemetry.Topics
{
    public sealed class EngineTopic : Topic
    {
        public override string Name { get { return "engines"; } }

        // Per-engine position jitter threshold. Physics wobble on an
        // unflexed rigid vessel sits well under a millimetre; 1 cm
        // gives comfortable headroom without hiding real part shifts
        // from docking / stage separation.
        private const float PosEpsilon = 0.01f;

        internal struct EngineState
        {
            public string Id;
            public float MapX, MapY;
            public byte Status;
            public float MaxThrust;
        }

        private string _vesselId = "";
        private readonly List<EngineState> _engines = new List<EngineState>();
        // Scratch list reused across frames to avoid allocating on
        // every sample. Sized to match the active vessel.
        private readonly List<EngineState> _scratch = new List<EngineState>();

        private void Update()
        {
            if (FlightGlobals.fetch == null) return;
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return;
            Transform refT = v.ReferenceTransform;
            if (refT == null) return;

            _scratch.Clear();
            Vector3 vesselPos = v.transform.position;
            for (int i = 0; i < v.Parts.Count; i++)
            {
                Part p = v.Parts[i];
                ModuleEngines eng = p.FindModuleImplementing<ModuleEngines>();
                if (eng == null) continue;

                // World → body-local, relative to the vessel reference
                // frame origin. We only need direction-of-offset, so
                // InverseTransformDirection is the right operator
                // (scale-invariant, and the reference frame's origin
                // is vessel-local).
                Vector3 rel = p.transform.position - vesselPos;
                Vector3 local = refT.InverseTransformDirection(rel);

                _scratch.Add(new EngineState
                {
                    Id = p.flightID.ToString(),
                    MapX = local.x,
                    MapY = local.z,
                    Status = Classify(eng, p),
                    MaxThrust = eng.maxThrust,
                });
            }

            string nextVesselId = v.id.ToString("D");
            if (nextVesselId != _vesselId || HasMaterialChange(_engines, _scratch))
            {
                _vesselId = nextVesselId;
                _engines.Clear();
                for (int i = 0; i < _scratch.Count; i++) _engines.Add(_scratch[i]);
                MarkDirty();
            }
        }

        private static byte Classify(ModuleEngines eng, Part p)
        {
            if (eng.EngineIgnited && eng.flameout) return 1;       // flameout
            if (eng.EngineIgnited && eng.finalThrust > 0f) return 0; // burning
            if (!eng.EngineIgnited && p.State == PartStates.IDLE) return 3; // shutdown
            return 2; // failed / involuntarily off — best-effort
        }

        private static bool HasMaterialChange(
            List<EngineState> prev, List<EngineState> next)
        {
            if (prev.Count != next.Count) return true;
            for (int i = 0; i < next.Count; i++)
            {
                EngineState a = prev[i];
                EngineState b = next[i];
                if (a.Id != b.Id) return true;
                if (a.Status != b.Status) return true;
                if (Mathf.Abs(a.MapX - b.MapX) > PosEpsilon) return true;
                if (Mathf.Abs(a.MapY - b.MapY) > PosEpsilon) return true;
            }
            return false;
        }

        public override void WriteData(StringBuilder sb)
        {
            sb.Append('[');
            Json.WriteString(sb, _vesselId);
            sb.Append(',');
            sb.Append('[');
            for (int i = 0; i < _engines.Count; i++)
            {
                if (i > 0) sb.Append(',');
                EngineState e = _engines[i];
                sb.Append('[');
                Json.WriteString(sb, e.Id);
                sb.Append(',');
                Json.WriteFloat(sb, e.MapX);
                sb.Append(',');
                Json.WriteFloat(sb, e.MapY);
                sb.Append(',');
                sb.Append(e.Status);
                sb.Append(',');
                Json.WriteFloat(sb, e.MaxThrust);
                sb.Append(']');
            }
            sb.Append(']');
            sb.Append(']');
        }
    }
}
