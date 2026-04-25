// Per-engine telemetry for the active vessel. Sole source of engine
// state on the wire: position + status + thrust + Isp + crossfeed set
// + per-propellant totals. The UI uses these fields to both render
// the SpaceX-livestream-style engine map and to compute fuel-group
// partitioning (which engines share a tank set) — grouping used to
// live on the C# side but is now a UI concern, so this topic ships
// the raw data unaggregated.
//
// Coordinate convention. Each engine is projected into vessel body
// frame via `ReferenceTransform.InverseTransformDirection`, then
// we keep (x, z) — the plane perpendicular to the up-stack axis
// (+Y). That yields a bottom-up orthographic view, which is the
// natural "engine map" orientation. Units: meters.
//
// Status byte:
//   0 = burning   (activated, producing non-zero thrust)
//   1 = flameout  (activated but starved of propellant)
//   2 = failed    (involuntarily off — best-effort; stock KSP has
//                  no first-class damage state, so we infer)
//   3 = shutdown  (off because it hasn't been staged yet / idle)
//   4 = idle      (activated and healthy but commanded throttle is
//                  zero — engine is ready to fire, just not
//                  currently producing thrust)
//
// "activated" here is KSP's `ModuleEngines.EngineIgnited` — the
// engine has been turned on by staging or an action group, not
// that the bell is literally combusting fuel.
//
// Structural vs per-frame data. The crossfeed part set and the
// propellant list for an engine only change on structure events
// (staging, docking, flow toggles, multi-mode switch). We cache
// that structure and rebuild it on the same KSP GameEvents the
// old CurrentStageTopic subscribed to. Per frame we just re-sum
// fuel amounts off cached `PartResource` refs, which is bounded
// and allocation-free.
//
// Wire format (positional array):
//   data: [vesselId, [
//     [id, mapX, mapY, status, throttle, maxThrust, isp,
//      [crossfeedPartId, ...],
//      [[propName, propAbbr, amount, capacity], ...]
//     ], ...
//   ]]
//
//     throttle  : per-engine post-everything throttle, 0..1. Sourced
//                 from `ModuleEngines.currentThrottle`, which already
//                 accounts for vessel throttle × thrust-limiter
//                 (`thrustPercentage`) and the engine's response
//                 curves. Forced to 0 unless status == burning so
//                 flamed-out / shutdown engines don't show stale
//                 commanded values.
//     maxThrust : configured maximum thrust at standard conditions
//                 (vacuum for most engines), kN. Stable across flight
//                 — the client uses it to scale circle radius so the
//                 engine map reads as a bubble chart where area
//                 encodes thrust magnitude.
//     isp       : atmo-adjusted Isp, seconds — `atmosphereCurve`
//                 evaluated at the vessel's current static pressure.
//                 Works regardless of ignition state.
//     crossfeedPartId : stringified `Part.flightID` of each part in
//                       the engine's crossfeed-reachable set.
//                       Sorted ascending for stable identity.
//     propellants : one entry per propellant, sorted by KSP resource
//                   id. Amount/capacity are summed across the tanks
//                   feeding this engine for that propellant.

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
        // Isp changes smoothly with altitude. 0.1 s is finer than the
        // user can read off a gauge but coarse enough that the topic
        // doesn't re-emit on every physics tick during climb.
        private const float IspEpsilon = 0.1f;
        // Throttle changes continuously when the pilot rides the
        // slider. 1 % is finer than the rosette dot's visible opacity
        // step (which spans 25–100 % brightness for throttle 0..1) and
        // keeps the topic from spamming on every physics tick during a
        // smooth ramp.
        private const float ThrottleEpsilon = 0.01f;
        // Per-engine, per-propellant fuel-level jitter threshold as a
        // fraction of capacity. Matches the old CurrentStageTopic
        // `FuelFractionEpsilon`.
        private const double FuelFractionEpsilon = 0.005;

        internal struct EngineFrame
        {
            public string Id;
            public float MapX, MapY;
            public byte Status;
            public float Throttle;
            public float MaxThrust;
            public float Isp;
            public List<string> CrossfeedPartIds;
            public List<PropellantFrame> Propellants;
        }

        internal struct PropellantFrame
        {
            public int ResourceId;
            public string Name;
            public string Abbreviation;
            public double Amount;
            public double Capacity;
        }

        // Structural cache per engine — rebuilt on KSP structure events
        // (staging / docking / flow changes). Holds direct
        // `PartResource` references so per-frame fuel summation is a
        // bounded, allocation-free inner loop.
        private class EngineStructure
        {
            public Part Part;
            public ModuleEngines Module;
            public List<string> CrossfeedPartIds = new List<string>();
            public List<PropellantStructure> Propellants = new List<PropellantStructure>();
        }

        private struct PropellantStructure
        {
            public int ResourceId;
            public string Name;
            public string Abbreviation;
            public List<PartResource> Resources;
        }

        // ---- Live state we publish ----
        private string _vesselId = "";
        private readonly List<EngineFrame> _engines = new List<EngineFrame>();

        // ---- Structural cache (rebuilt on vessel events, not per frame) ----
        private bool _structureDirty = true;
        private Vessel _cachedVessel;
        private readonly List<EngineStructure> _structure = new List<EngineStructure>();

        // Scratch list reused across frames to avoid allocating on
        // every sample. Sized to match the active vessel.
        private readonly List<EngineFrame> _scratch = new List<EngineFrame>();

        protected override void OnEnable()
        {
            base.OnEnable();
            GameEvents.onVesselWasModified.Add(OnVesselStructureChanged);
            GameEvents.onStageActivate.Add(OnStageChanged);
            GameEvents.onDockingComplete.Add(OnDockingComplete);
            GameEvents.onVesselsUndocking.Add(OnUndocking);
            GameEvents.onPartCrossfeedStateChange.Add(OnPartChanged);
            GameEvents.onPartResourceFlowStateChange.Add(OnFlowChanged);
            GameEvents.onPartResourceListChange.Add(OnPartChanged);
            GameEvents.onMultiModeEngineSwitchActive.Add(OnEngineModeSwitched);
            GameEvents.onVesselChange.Add(OnVesselChange);
            GameEvents.StageManager.OnGUIStageSequenceModified.Add(OnStageSequenceModified);
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            GameEvents.onVesselWasModified.Remove(OnVesselStructureChanged);
            GameEvents.onStageActivate.Remove(OnStageChanged);
            GameEvents.onDockingComplete.Remove(OnDockingComplete);
            GameEvents.onVesselsUndocking.Remove(OnUndocking);
            GameEvents.onPartCrossfeedStateChange.Remove(OnPartChanged);
            GameEvents.onPartResourceFlowStateChange.Remove(OnFlowChanged);
            GameEvents.onPartResourceListChange.Remove(OnPartChanged);
            GameEvents.onMultiModeEngineSwitchActive.Remove(OnEngineModeSwitched);
            GameEvents.onVesselChange.Remove(OnVesselChange);
            GameEvents.StageManager.OnGUIStageSequenceModified.Remove(OnStageSequenceModified);
        }

        private void OnVesselStructureChanged(Vessel v) { if (v == FlightGlobals.ActiveVessel) _structureDirty = true; }
        private void OnStageChanged(int _) { _structureDirty = true; }
        private void OnDockingComplete(GameEvents.FromToAction<Part, Part> _) { _structureDirty = true; }
        private void OnUndocking(Vessel _, Vessel __) { _structureDirty = true; }
        private void OnPartChanged(Part _) { _structureDirty = true; }
        private void OnFlowChanged(GameEvents.HostedFromToAction<PartResource, bool> _) { _structureDirty = true; }
        private void OnEngineModeSwitched(MultiModeEngine _) { _structureDirty = true; }
        private void OnVesselChange(Vessel _) { _structureDirty = true; }
        private void OnStageSequenceModified() { _structureDirty = true; }

        private void Update()
        {
            if (FlightGlobals.fetch == null) return;
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return;
            Transform refT = v.ReferenceTransform;
            if (refT == null) return;

            if (v != _cachedVessel)
            {
                _cachedVessel = v;
                _structureDirty = true;
            }

            if (_structureDirty)
            {
                RebuildStructure(v);
                _structureDirty = false;
            }

            _scratch.Clear();
            Vector3 vesselPos = v.transform.position;
            for (int i = 0; i < _structure.Count; i++)
            {
                EngineStructure es = _structure[i];
                if (es.Part == null || es.Module == null)
                {
                    _structureDirty = true;
                    return;
                }

                Vector3 rel = es.Part.transform.position - vesselPos;
                Vector3 local = refT.InverseTransformDirection(rel);
                float atmPressure = (float)es.Part.staticPressureAtm;

                List<PropellantFrame> propellants = new List<PropellantFrame>(es.Propellants.Count);
                bool stale = false;
                for (int p = 0; p < es.Propellants.Count; p++)
                {
                    PropellantStructure ps = es.Propellants[p];
                    double amt = 0, cap = 0;
                    for (int k = 0; k < ps.Resources.Count; k++)
                    {
                        PartResource pr = ps.Resources[k];
                        if (pr == null || pr.part == null) { stale = true; break; }
                        amt += pr.amount;
                        cap += pr.maxAmount;
                    }
                    if (stale) break;
                    propellants.Add(new PropellantFrame
                    {
                        ResourceId = ps.ResourceId,
                        Name = ps.Name,
                        Abbreviation = ps.Abbreviation,
                        Amount = amt,
                        Capacity = cap,
                    });
                }
                if (stale)
                {
                    _structureDirty = true;
                    return;
                }

                float isp = es.Module.atmosphereCurve != null
                    ? es.Module.atmosphereCurve.Evaluate(atmPressure)
                    : 0f;

                byte status = Classify(es.Module, es.Part);
                // Only burning engines have a meaningful throttle.
                // currentThrottle on a flamed-out / shutdown engine
                // can hold a stale commanded value; gating to 0 keeps
                // the rosette dot dark for any non-firing engine.
                float throttle = status == 0
                    ? Mathf.Clamp01(es.Module.currentThrottle)
                    : 0f;

                _scratch.Add(new EngineFrame
                {
                    Id = es.Part.flightID.ToString(),
                    MapX = local.x,
                    MapY = local.z,
                    Status = status,
                    Throttle = throttle,
                    MaxThrust = es.Module.maxThrust,
                    Isp = isp,
                    CrossfeedPartIds = es.CrossfeedPartIds,
                    Propellants = propellants,
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
            if (eng.EngineIgnited) return 4;                       // idle (armed, throttle 0)
            if (!eng.EngineIgnited && p.State == PartStates.IDLE) return 3; // shutdown
            return 2; // failed / involuntarily off — best-effort
        }

        private static bool HasMaterialChange(
            List<EngineFrame> prev, List<EngineFrame> next)
        {
            if (prev.Count != next.Count) return true;
            for (int i = 0; i < next.Count; i++)
            {
                EngineFrame a = prev[i];
                EngineFrame b = next[i];
                if (a.Id != b.Id) return true;
                if (a.Status != b.Status) return true;
                if (Mathf.Abs(a.Throttle - b.Throttle) > ThrottleEpsilon) return true;
                if (Mathf.Abs(a.MapX - b.MapX) > PosEpsilon) return true;
                if (Mathf.Abs(a.MapY - b.MapY) > PosEpsilon) return true;
                if (Mathf.Abs(a.Isp - b.Isp) > IspEpsilon) return true;
                // Crossfeed list identity — reference-equal when the
                // structural cache hasn't been rebuilt. A rebuild
                // swaps in a fresh list.
                if (!System.Object.ReferenceEquals(a.CrossfeedPartIds, b.CrossfeedPartIds))
                    return true;
                if (a.Propellants.Count != b.Propellants.Count) return true;
                for (int p = 0; p < b.Propellants.Count; p++)
                {
                    PropellantFrame pa = a.Propellants[p];
                    PropellantFrame pb = b.Propellants[p];
                    if (pa.ResourceId != pb.ResourceId) return true;
                    double capRef = System.Math.Max(pa.Capacity, pb.Capacity);
                    if (capRef <= 1e-9) continue;
                    double dAmt = System.Math.Abs(pa.Amount - pb.Amount) / capRef;
                    double dCap = System.Math.Abs(pa.Capacity - pb.Capacity) / capRef;
                    if (dAmt > FuelFractionEpsilon) return true;
                    if (dCap > FuelFractionEpsilon) return true;
                }
            }
            return false;
        }

        private void RebuildStructure(Vessel v)
        {
            _structure.Clear();

            List<ModuleEngines> engines = v.FindPartModulesImplementing<ModuleEngines>();
            for (int e = 0; e < engines.Count; e++)
            {
                ModuleEngines eng = engines[e];
                if (eng == null || eng.part == null) continue;

                PartSet crossfeed = eng.part.crossfeedPartSet;
                if (crossfeed == null) continue;

                EngineStructure es = new EngineStructure
                {
                    Part = eng.part,
                    Module = eng,
                };

                // Crossfeed parts — propellant-agnostic reachable set,
                // sorted for stable identity. Done once per structure
                // epoch; per-frame we just ship the same list.
                HashSet<uint> seen = new HashSet<uint>();
                foreach (Part fp in crossfeed.GetParts())
                {
                    if (fp == null) continue;
                    seen.Add(fp.flightID);
                }
                List<uint> sortedIds = new List<uint>(seen);
                sortedIds.Sort();
                for (int i = 0; i < sortedIds.Count; i++)
                {
                    es.CrossfeedPartIds.Add(sortedIds[i].ToString());
                }

                // Propellants — per-propellant tank list via stock
                // KSP's resolver, sorted by resource id so two engines
                // with the same propellants in a different declared
                // order produce identical structures.
                List<Propellant> props = eng.propellants;
                if (props == null || props.Count == 0) { _structure.Add(es); continue; }

                List<Propellant> sortedProps = new List<Propellant>(props);
                sortedProps.Sort((a, b) => a.id.CompareTo(b.id));

                for (int p = 0; p < sortedProps.Count; p++)
                {
                    Propellant prop = sortedProps[p];
                    PartSet.ResourcePrioritySet rps;
                    try { rps = crossfeed.GetResourceList(prop.id, true, false); }
                    catch { rps = null; }

                    List<PartResource> resList = new List<PartResource>();
                    if (rps != null && rps.set != null)
                    {
                        foreach (PartResource pr in rps.set)
                        {
                            if (pr == null || pr.part == null) continue;
                            resList.Add(pr);
                        }
                    }

                    string abbr = AbbreviationFor(prop);
                    es.Propellants.Add(new PropellantStructure
                    {
                        ResourceId = prop.id,
                        Name = prop.name,
                        Abbreviation = abbr,
                        Resources = resList,
                    });
                }

                _structure.Add(es);
            }
        }

        private static string AbbreviationFor(Propellant prop)
        {
            PartResourceDefinition def = PartResourceLibrary.Instance != null
                ? PartResourceLibrary.Instance.GetDefinition(prop.id)
                : null;
            if (def != null && !string.IsNullOrEmpty(def.abbreviation))
                return def.abbreviation;
            return prop.name;
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
                EngineFrame e = _engines[i];
                sb.Append('[');
                Json.WriteString(sb, e.Id);
                sb.Append(',');
                Json.WriteFloat(sb, e.MapX);
                sb.Append(',');
                Json.WriteFloat(sb, e.MapY);
                sb.Append(',');
                sb.Append(e.Status);
                sb.Append(',');
                Json.WriteFloat(sb, e.Throttle);
                sb.Append(',');
                Json.WriteFloat(sb, e.MaxThrust);
                sb.Append(',');
                Json.WriteFloat(sb, e.Isp);
                sb.Append(',');
                sb.Append('[');
                for (int j = 0; j < e.CrossfeedPartIds.Count; j++)
                {
                    if (j > 0) sb.Append(',');
                    Json.WriteString(sb, e.CrossfeedPartIds[j]);
                }
                sb.Append(']');
                sb.Append(',');
                sb.Append('[');
                for (int j = 0; j < e.Propellants.Count; j++)
                {
                    if (j > 0) sb.Append(',');
                    PropellantFrame pf = e.Propellants[j];
                    sb.Append('[');
                    Json.WriteString(sb, pf.Name);
                    sb.Append(',');
                    Json.WriteString(sb, pf.Abbreviation);
                    sb.Append(',');
                    Json.WriteDouble(sb, pf.Amount);
                    sb.Append(',');
                    Json.WriteDouble(sb, pf.Capacity);
                    sb.Append(']');
                }
                sb.Append(']');
                sb.Append(']');
            }
            sb.Append(']');
            sb.Append(']');
        }
    }
}
