// Current-stage telemetry for the active vessel. Drives the
// CurrentStage panel above Propulsion in the left staging stack:
// stage Δv, stage TWR, and per-fuel-group engine icons + gauges.
//
// ------------------------------------------------------------
// Engine grouping
// ------------------------------------------------------------
// Two engines belong to the same fuel group iff they draw from the
// same set of tanks for the same set of propellants. We resolve
// this precisely via `Part.crossfeedPartSet.GetResourceList(...)`,
// which enumerates the exact `PartResource` instances feeding the
// engine for each propellant. Engines are grouped by a canonical
// signature built from those sets; the group's gauges are the sum
// of each `PartResource.amount` / `maxAmount` across the tank
// union.
//
// ------------------------------------------------------------
// Performance — why we cache structure between vessel events
// ------------------------------------------------------------
// The *structure* (which tanks feed which engines, which engines
// share a tank set) only changes when the vessel changes: staging,
// docking, undocking, crossfeed toggles, flow-state toggles,
// multi-mode engine switch, stage-sequence edits. All of those
// raise KSP GameEvents (see `VesselDeltaV.Start()` for the
// canonical list). We subscribe to those events, flip a
// `_structureDirty` flag, and only walk the part tree +
// `GetResourceList` + rebuild groups when the flag is set.
//
// Per frame we just iterate the cached `EngineGroupCache` list and
// sum `PartResource.amount` / `maxAmount` — bounded, allocation-
// free, and cheap. Scalars (`deltaVActual`, `TWRActual`) come from
// `vessel.VesselDeltaV.GetStage(...)` which is already cached
// inside KSP; we don't trigger its simulation.
//
// Stale reference guard: we hold direct `PartResource` refs. If a
// tank disappears between events without raising one, we detect
// it during summation (`pr == null || pr.part == null`) and force
// a structure rebuild the next frame.
//
// ------------------------------------------------------------
// Wire format (positional JSON array)
// ------------------------------------------------------------
//   data: [stageIdx, deltaVStage, twrStage,
//          [ [ [engineId, ...], [[resName, avail, cap], ...] ], ... ]]

using System.Collections.Generic;
using System.Text;
using Dragonglass.Telemetry.Util;
using UnityEngine;

namespace Dragonglass.Telemetry.Topics
{
    public sealed class CurrentStageTopic : Topic
    {
        public override string Name { get { return "currentStage"; } }

        // Material-change thresholds for dirty-guarding the wire.
        // Scalars tolerate sub-1-unit jitter; fuel levels tolerate
        // half a percent of capacity (prevents a paused craft from
        // chattering every frame).
        private const double DvEpsilon = 0.5;
        private const float TwrEpsilon = 0.005f;
        private const double FuelFractionEpsilon = 0.005;

        // ---- Live state we publish ----
        private int _stageIdx = -1;
        private double _deltaVStage;
        private float _twrStage;
        private readonly List<PublishedGroup> _published = new List<PublishedGroup>();

        // ---- Structural cache (rebuilt on vessel events, not per frame) ----
        private bool _structureDirty = true;
        private Vessel _cachedVessel;
        private readonly List<EngineGroupCache> _groups = new List<EngineGroupCache>();

        private struct EngineGroupCache
        {
            public string SignatureKey;
            public List<Part> Engines;
            // Parallel arrays: one entry per propellant in the
            // group's propellant set. `Sources[i]` lists every
            // PartResource in the union of tanks feeding this
            // group for propellant i.
            public List<ResourceSourceSet> Sources;
        }

        private struct ResourceSourceSet
        {
            public int ResourceId;
            public string ResourceName;
            public List<PartResource> Resources;
        }

        private struct PublishedGroup
        {
            public List<uint> EngineIds;
            public List<PublishedFuel> Fuels;
        }

        private struct PublishedFuel
        {
            public string ResourceName;
            public double Available;
            public double Capacity;
        }

        // ---- Lifecycle: subscribe / unsubscribe to KSP structure events ----

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

        // ---- Per-frame update ----

        private void Update()
        {
            if (FlightGlobals.fetch == null) return;
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return;

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

            int stageIdx = v.currentStage;
            double dv = 0;
            float twr = 0;
            VesselDeltaV vdv = v.VesselDeltaV;
            if (vdv != null)
            {
                DeltaVStageInfo stage = vdv.GetStage(stageIdx);
                if (stage != null)
                {
                    dv = stage.deltaVActual;
                    twr = stage.TWRActual;
                }
            }

            bool changed = false;

            if (_stageIdx != stageIdx) { _stageIdx = stageIdx; changed = true; }
            if (System.Math.Abs(_deltaVStage - dv) > DvEpsilon) { _deltaVStage = dv; changed = true; }
            if (Mathf.Abs(_twrStage - twr) > TwrEpsilon) { _twrStage = twr; changed = true; }

            // Sum per-group per-propellant from the cache. If we
            // see a stale PartResource ref, abandon summation and
            // force a rebuild next frame — the snapshot from last
            // frame stays on the wire until then.
            bool fuelChanged;
            bool stale = !TryRefreshFuelLevels(out fuelChanged);
            if (stale)
            {
                _structureDirty = true;
            }
            else if (fuelChanged)
            {
                changed = true;
            }

            if (changed) MarkDirty();
        }

        private bool TryRefreshFuelLevels(out bool changed)
        {
            changed = false;

            // Ensure _published shape matches _groups shape.
            if (_published.Count != _groups.Count)
            {
                _published.Clear();
                for (int i = 0; i < _groups.Count; i++) _published.Add(NewPublishedGroup(_groups[i]));
                changed = true;
            }

            for (int g = 0; g < _groups.Count; g++)
            {
                EngineGroupCache gc = _groups[g];
                PublishedGroup pg = _published[g];

                for (int p = 0; p < gc.Sources.Count; p++)
                {
                    ResourceSourceSet rss = gc.Sources[p];
                    double avail = 0;
                    double cap = 0;
                    for (int k = 0; k < rss.Resources.Count; k++)
                    {
                        PartResource pr = rss.Resources[k];
                        if (pr == null || pr.part == null) return false; // stale — trigger rebuild
                        avail += pr.amount;
                        cap += pr.maxAmount;
                    }

                    PublishedFuel pf = pg.Fuels[p];
                    double frac = cap > 0 ? avail / cap : 0;
                    double prevFrac = pf.Capacity > 0 ? pf.Available / pf.Capacity : 0;
                    if (System.Math.Abs(frac - prevFrac) > FuelFractionEpsilon
                        || System.Math.Abs(cap - pf.Capacity) > 0.1)
                    {
                        pf.Available = avail;
                        pf.Capacity = cap;
                        pg.Fuels[p] = pf;
                        changed = true;
                    }
                }
            }
            return true;
        }

        private static PublishedGroup NewPublishedGroup(EngineGroupCache gc)
        {
            PublishedGroup pg;
            pg.EngineIds = new List<uint>(gc.Engines.Count);
            for (int i = 0; i < gc.Engines.Count; i++) pg.EngineIds.Add(gc.Engines[i].flightID);
            pg.Fuels = new List<PublishedFuel>(gc.Sources.Count);
            for (int i = 0; i < gc.Sources.Count; i++)
            {
                pg.Fuels.Add(new PublishedFuel { ResourceName = gc.Sources[i].ResourceName });
            }
            return pg;
        }

        // ---- Structure rebuild ----

        private void RebuildStructure(Vessel v)
        {
            _groups.Clear();
            _published.Clear();

            // Bucket engines by signature. Signature =
            // "resId:tankId,tankId|resId:tankId…" with both levels
            // sorted so two engines with the same tank set always
            // produce the same string key.
            Dictionary<string, int> keyToGroup = new Dictionary<string, int>();

            List<ModuleEngines> engines = v.FindPartModulesImplementing<ModuleEngines>();
            StringBuilder keyBuf = new StringBuilder();

            for (int e = 0; e < engines.Count; e++)
            {
                ModuleEngines eng = engines[e];
                if (eng == null || eng.part == null) continue;

                PartSet crossfeed = eng.part.crossfeedPartSet;
                if (crossfeed == null) continue;

                // Collect (resId, sorted tankIds, PartResource list) per propellant.
                List<Propellant> propellants = eng.propellants;
                if (propellants == null || propellants.Count == 0) continue;

                // Sort propellants by id so the signature is stable
                // across engines that declare propellants in
                // different orders (same resources, different
                // config ordering).
                List<Propellant> sortedProps = new List<Propellant>(propellants);
                sortedProps.Sort((a, b) => a.id.CompareTo(b.id));

                keyBuf.Length = 0;
                List<ResourceSourceSet> sources = new List<ResourceSourceSet>(sortedProps.Count);

                for (int p = 0; p < sortedProps.Count; p++)
                {
                    Propellant prop = sortedProps[p];
                    PartSet.ResourcePrioritySet rps;
                    try
                    {
                        rps = crossfeed.GetResourceList(prop.id, true, false);
                    }
                    catch
                    {
                        rps = null;
                    }

                    List<PartResource> resList = new List<PartResource>();
                    List<uint> tankIds = new List<uint>();
                    if (rps != null && rps.set != null)
                    {
                        foreach (PartResource pr in rps.set)
                        {
                            if (pr == null || pr.part == null) continue;
                            resList.Add(pr);
                            tankIds.Add(pr.part.flightID);
                        }
                    }
                    tankIds.Sort();

                    if (p > 0) keyBuf.Append('|');
                    keyBuf.Append(prop.id).Append(':');
                    for (int i = 0; i < tankIds.Count; i++)
                    {
                        if (i > 0) keyBuf.Append(',');
                        keyBuf.Append(tankIds[i]);
                    }

                    sources.Add(new ResourceSourceSet
                    {
                        ResourceId = prop.id,
                        ResourceName = prop.name,
                        Resources = resList,
                    });
                }

                string key = keyBuf.ToString();

                int groupIdx;
                if (!keyToGroup.TryGetValue(key, out groupIdx))
                {
                    groupIdx = _groups.Count;
                    keyToGroup[key] = groupIdx;
                    _groups.Add(new EngineGroupCache
                    {
                        SignatureKey = key,
                        Engines = new List<Part>(),
                        Sources = sources,
                    });
                }
                _groups[groupIdx].Engines.Add(eng.part);
            }

            // Initialise published snapshots from fresh structure.
            for (int i = 0; i < _groups.Count; i++) _published.Add(NewPublishedGroup(_groups[i]));
        }

        // ---- Wire ----

        public override void WriteData(StringBuilder sb)
        {
            sb.Append('[');
            Json.WriteLong(sb, _stageIdx);
            sb.Append(',');
            Json.WriteDouble(sb, _deltaVStage);
            sb.Append(',');
            Json.WriteFloat(sb, _twrStage);
            sb.Append(',');

            sb.Append('[');
            for (int g = 0; g < _published.Count; g++)
            {
                if (g > 0) sb.Append(',');
                PublishedGroup pg = _published[g];
                sb.Append('[');

                // Engine IDs
                sb.Append('[');
                for (int i = 0; i < pg.EngineIds.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Json.WriteString(sb, pg.EngineIds[i].ToString());
                }
                sb.Append(']');
                sb.Append(',');

                // Propellant totals
                sb.Append('[');
                for (int i = 0; i < pg.Fuels.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    PublishedFuel pf = pg.Fuels[i];
                    sb.Append('[');
                    Json.WriteString(sb, pf.ResourceName);
                    sb.Append(',');
                    Json.WriteDouble(sb, pf.Available);
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
