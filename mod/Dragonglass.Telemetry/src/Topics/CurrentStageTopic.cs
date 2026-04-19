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

        // When two groups share the same multiset of engine types and
        // their per-propellant stack totals (current + capacity)
        // match within 0.1 %, they collapse into one group — e.g. two
        // symmetric boosters on physically-separate LFO tanks that
        // start with identical fuel loads and drain in lockstep.
        // Without this pass they would otherwise render as two
        // visually-redundant gauge rows that track each other
        // exactly.
        private const double MergeFuelThreshold = 0.001;

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
            // Pre-merge "components" — each one is the group that
            // was fused into this aggregate. A group that was never
            // merged has exactly one component (itself). A group
            // built from N merged pre-groups has N. Per-frame drift
            // checks compare each component's totals against the
            // others; when any pair diverges past the threshold,
            // the aggregate splits back into its components.
            public List<EngineGroupComponent> Components;
        }

        // Holds one pre-merge group's own tank-set, kept around so
        // the per-frame drift check in `TryRefreshFuelLevels` can
        // sum each component independently and detect when symmetric
        // stacks have started to diverge in flight.
        private class EngineGroupComponent
        {
            public string SignatureKey;
            public List<Part> Engines;
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

            // Drift check — split any merged group whose components
            // have diverged beyond the threshold. Only splits, never
            // re-merges: a re-merge could only happen on the next
            // vessel-structure event (RebuildStructure), so drift
            // decisions are strictly monotonic within a structure
            // epoch. That prevents threshold-boundary flicker.
            if (SplitDriftedMergedGroups()) changed = true;

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

            // Seed each group's Components list with itself, so the
            // per-frame drift check has the pre-merge constituents
            // to compare. The component shares list references with
            // the group because this group's own lists aren't
            // mutated post-grouping — merges allocate fresh lists
            // for the combined aggregate and leave components alone.
            for (int i = 0; i < _groups.Count; i++)
            {
                EngineGroupCache gc = _groups[i];
                gc.Components = new List<EngineGroupComponent>(1);
                gc.Components.Add(new EngineGroupComponent
                {
                    SignatureKey = gc.SignatureKey,
                    Engines = gc.Engines,
                    Sources = gc.Sources,
                });
                _groups[i] = gc;
            }

            // Second pass: collapse groups that draw from *different*
            // tank sets but are otherwise indistinguishable to the
            // pilot — same engine types, same propellant set, and
            // tank-stack totals matching within MergeFuelThreshold.
            MergeEquivalentGroups();

            // Initialise published snapshots from fresh structure.
            for (int i = 0; i < _groups.Count; i++) _published.Add(NewPublishedGroup(_groups[i]));
        }

        // Iteratively merge compatible group pairs until no more
        // merges are possible. The inner loop is O(N²) per pass, and
        // this pass runs only on vessel-structure events (staging /
        // docking / flow changes), so the cost is negligible.
        //
        // Merges happen only here, not per frame. The symmetric
        // operation — unmerging when a previously-matched aggregate
        // has drifted — runs every frame in
        // `SplitDriftedMergedGroups`. That asymmetry is deliberate:
        // merging at structure-rebuild time + splitting on drift
        // together mean the grouping decision is strictly
        // monotonic within a structure epoch (only gains splits,
        // never loses them), so a gauge can't flicker across the
        // threshold boundary as fuel burns.
        private void MergeEquivalentGroups()
        {
            bool mergedAny = true;
            while (mergedAny && _groups.Count > 1)
            {
                mergedAny = false;
                for (int i = 0; i < _groups.Count && !mergedAny; i++)
                {
                    for (int j = i + 1; j < _groups.Count; j++)
                    {
                        if (CanMergeGroups(_groups[i], _groups[j]))
                        {
                            _groups[i] = MergeGroups(_groups[i], _groups[j]);
                            _groups.RemoveAt(j);
                            mergedAny = true;
                            break;
                        }
                    }
                }
            }
        }

        private static bool CanMergeGroups(EngineGroupCache a, EngineGroupCache b)
        {
            // Same multiset of engine types. Sort part names so the
            // comparison doesn't depend on the order engines were
            // added in.
            if (a.Engines.Count != b.Engines.Count) return false;
            List<string> aNames = new List<string>(a.Engines.Count);
            List<string> bNames = new List<string>(b.Engines.Count);
            for (int i = 0; i < a.Engines.Count; i++) aNames.Add(PartName(a.Engines[i]));
            for (int i = 0; i < b.Engines.Count; i++) bNames.Add(PartName(b.Engines[i]));
            aNames.Sort(System.StringComparer.Ordinal);
            bNames.Sort(System.StringComparer.Ordinal);
            for (int i = 0; i < aNames.Count; i++)
            {
                if (aNames[i] != bNames[i]) return false;
            }

            // Same propellant set. Sources lists are already sorted by
            // resource id when constructed in RebuildStructure, so a
            // position-by-position comparison suffices.
            if (a.Sources.Count != b.Sources.Count) return false;
            for (int i = 0; i < a.Sources.Count; i++)
            {
                if (a.Sources[i].ResourceId != b.Sources[i].ResourceId) return false;
            }

            // Tank-stack totals match within threshold — both current
            // and capacity, per resource. The threshold is relative
            // to the larger capacity of the pair so comparisons are
            // invariant across absolute tank sizes.
            for (int i = 0; i < a.Sources.Count; i++)
            {
                double aAmt = 0, aCap = 0;
                for (int k = 0; k < a.Sources[i].Resources.Count; k++)
                {
                    PartResource pr = a.Sources[i].Resources[k];
                    if (pr == null || pr.part == null) return false;
                    aAmt += pr.amount;
                    aCap += pr.maxAmount;
                }
                double bAmt = 0, bCap = 0;
                for (int k = 0; k < b.Sources[i].Resources.Count; k++)
                {
                    PartResource pr = b.Sources[i].Resources[k];
                    if (pr == null || pr.part == null) return false;
                    bAmt += pr.amount;
                    bCap += pr.maxAmount;
                }
                double scale = System.Math.Max(aCap, bCap);
                if (scale <= 1e-9) continue; // both essentially empty tanks — treat as match
                if (System.Math.Abs(aAmt - bAmt) / scale > MergeFuelThreshold) return false;
                if (System.Math.Abs(aCap - bCap) / scale > MergeFuelThreshold) return false;
            }

            return true;
        }

        private static EngineGroupCache MergeGroups(EngineGroupCache a, EngineGroupCache b)
        {
            List<Part> engines = new List<Part>(a.Engines.Count + b.Engines.Count);
            engines.AddRange(a.Engines);
            engines.AddRange(b.Engines);

            // Union each resource's tank list. Order of the Sources
            // list is preserved (same resource-id ordering as both
            // inputs). The summed amounts / capacities fall out
            // naturally at read time in TryRefreshFuelLevels.
            List<ResourceSourceSet> sources = new List<ResourceSourceSet>(a.Sources.Count);
            for (int i = 0; i < a.Sources.Count; i++)
            {
                ResourceSourceSet merged;
                merged.ResourceId = a.Sources[i].ResourceId;
                merged.ResourceName = a.Sources[i].ResourceName;
                merged.Resources = new List<PartResource>(
                    a.Sources[i].Resources.Count + b.Sources[i].Resources.Count);
                merged.Resources.AddRange(a.Sources[i].Resources);
                merged.Resources.AddRange(b.Sources[i].Resources);
                sources.Add(merged);
            }

            // Flatten components — the merged group's constituents
            // are the union of both inputs' constituents. An
            // aggregate built from three chained merges tracks all
            // three pre-merge pieces, not just the last pair.
            List<EngineGroupComponent> components = new List<EngineGroupComponent>(
                a.Components.Count + b.Components.Count);
            components.AddRange(a.Components);
            components.AddRange(b.Components);

            return new EngineGroupCache
            {
                SignatureKey = a.SignatureKey + "+" + b.SignatureKey,
                Engines = engines,
                Sources = sources,
                Components = components,
            };
        }

        private static string PartName(Part p)
        {
            if (p == null) return string.Empty;
            return p.partInfo != null ? p.partInfo.name : p.name;
        }

        // Per-frame drift check for merged groups. For each merged
        // aggregate (Components.Count ≥ 2), take the first component
        // as the "norm" and compare every other component against
        // it. Components that deviate past `MergeFuelThreshold` are
        // kicked out as their own aggregate (one kicked group holds
        // all kicked components together, regardless of whether they
        // match each other — the next frame's pass decides that).
        //
        // Cost per frame: O(G_merged × M × P × K) where G_merged is
        // the number of currently-merged aggregates, M is each
        // aggregate's component count (≤ the few symmetric stacks a
        // craft typically carries), P is propellants (1–3) and K is
        // tanks per component. Singleton groups — the usual case —
        // are skipped on the Count < 2 guard.
        //
        // Never merges: split decisions are strictly monotonic
        // within a structure epoch (only new splits, never un-
        // splits), so a gauge can't flicker across the threshold
        // as fuel burns. The matched-stacks state is only
        // re-entered on the next vessel-structure event when
        // `MergeEquivalentGroups` re-runs from scratch.
        //
        // Convergence: if the kicked group itself contains further
        // drift, it settles over subsequent frames — each pass peels
        // off divergent components against the kicked group's own
        // (new) first component. For realistic layouts this
        // stabilises in 1–2 frames.
        //
        // Returns true iff at least one group was split.
        private bool SplitDriftedMergedGroups()
        {
            bool splitAny = false;
            // Walk backward so in-place splits don't renumber indices
            // we haven't visited yet.
            for (int g = _groups.Count - 1; g >= 0; g--)
            {
                EngineGroupCache gc = _groups[g];
                if (gc.Components.Count < 2) continue;

                List<EngineGroupComponent> kicked = FindDriftedComponents(gc);
                if (kicked == null || kicked.Count == 0) continue;

                // Partition: kept = everything not kicked, preserved
                // in the aggregate's original order.
                List<EngineGroupComponent> kept = new List<EngineGroupComponent>(
                    gc.Components.Count - kicked.Count);
                for (int i = 0; i < gc.Components.Count; i++)
                {
                    EngineGroupComponent c = gc.Components[i];
                    if (!kicked.Contains(c)) kept.Add(c);
                }

                _groups.RemoveAt(g);
                _groups.Insert(g, BuildGroupFromComponents(kept));
                _groups.Insert(g + 1, BuildGroupFromComponents(kicked));
                splitAny = true;
            }
            // Shape changed — drop the published snapshot so
            // TryRefreshFuelLevels rebuilds it to match the new
            // group count.
            if (splitAny) _published.Clear();
            return splitAny;
        }

        // Returns the list of components whose per-propellant totals
        // deviate from the group's first component (the "norm") by
        // more than `MergeFuelThreshold`, or null if none do. A
        // stale `PartResource` reference anywhere in the walk
        // aborts the check and triggers a structure rebuild.
        private List<EngineGroupComponent> FindDriftedComponents(EngineGroupCache group)
        {
            List<EngineGroupComponent> comps = group.Components;
            EngineGroupComponent norm = comps[0];
            int P = norm.Sources.Count;

            // Cache the norm's totals once — the same values get
            // compared against every other component.
            double[] normAmts = new double[P];
            double[] normCaps = new double[P];
            for (int p = 0; p < P; p++)
            {
                if (!SumResources(norm.Sources[p].Resources, out normAmts[p], out normCaps[p]))
                {
                    return null;
                }
            }

            List<EngineGroupComponent> drifted = null;
            for (int i = 1; i < comps.Count; i++)
            {
                EngineGroupComponent c = comps[i];
                bool kick = false;
                for (int p = 0; p < P; p++)
                {
                    double amt, cap;
                    if (!SumResources(c.Sources[p].Resources, out amt, out cap))
                    {
                        return null;
                    }
                    double scale = System.Math.Max(cap, normCaps[p]);
                    if (scale <= 1e-9) continue;
                    if (System.Math.Abs(amt - normAmts[p]) / scale > MergeFuelThreshold ||
                        System.Math.Abs(cap - normCaps[p]) / scale > MergeFuelThreshold)
                    {
                        kick = true;
                        break;
                    }
                }
                if (kick)
                {
                    if (drifted == null) drifted = new List<EngineGroupComponent>();
                    drifted.Add(c);
                }
            }
            return drifted;
        }

        // Reconstitute an `EngineGroupCache` from one or more
        // components. For a singleton the component's own lists are
        // reused directly (they're not mutated post-rebuild). For
        // multiple components we fold-left via `MergeGroups`, which
        // produces fresh combined Engines/Sources lists and a
        // flattened Components list on the result.
        private static EngineGroupCache BuildGroupFromComponents(List<EngineGroupComponent> comps)
        {
            EngineGroupCache acc = SingletonGroup(comps[0]);
            for (int i = 1; i < comps.Count; i++)
            {
                acc = MergeGroups(acc, SingletonGroup(comps[i]));
            }
            return acc;
        }

        private static EngineGroupCache SingletonGroup(EngineGroupComponent c)
        {
            return new EngineGroupCache
            {
                SignatureKey = c.SignatureKey,
                Engines = c.Engines,
                Sources = c.Sources,
                Components = new List<EngineGroupComponent>(1) { c },
            };
        }

        // Returns false if any resource reference has gone stale —
        // the caller treats that as "can't decide right now" and
        // the next structure event will rebuild cleanly.
        private bool SumResources(List<PartResource> resources, out double amount, out double capacity)
        {
            amount = 0;
            capacity = 0;
            for (int k = 0; k < resources.Count; k++)
            {
                PartResource pr = resources[k];
                if (pr == null || pr.part == null)
                {
                    _structureDirty = true;
                    return false;
                }
                amount += pr.amount;
                capacity += pr.maxAmount;
            }
            return true;
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
