// Per-stage telemetry for the active vessel.
//
// Drives the Dragonglass staging stack — a vertical list of stage
// cards, one per stage, each showing dV/TWR/burn-time plus the
// column of part icons (engines, decouplers, parachutes, clamps)
// that fire or separate at that stage. Mirrors the information
// density of KSP's stock stager but renders on the CEF side.
//
// Data source. dV/TWR/burn-time are read straight off
// `VesselDeltaV.OperatingStageInfo` — the list stock KSP already
// maintains, refreshed by its own simulator. We don't re-run the
// dV calc; we subscribe to `onDeltaVCalcsCompleted` so we sample
// after a fresh simulation pass.
//
// The parts list is built by iterating the active vessel's parts,
// keeping those with `hasStagingIcon && stagingOn`, and bucketing
// by `inverseStage` — the stage at which the part itself activates
// (engines ignite / decouplers fire / chutes deploy). This matches
// stock's own `StageGroup.Icons` composition.
//
// Staging transitions are force-emitted. When the player stages,
// `OperatingStageInfo` reshuffles but scalar dV/TWR numbers for
// the *new* current stage may be numerically close to what the
// previous frame already published for the *old* current stage
// (common case: similar second-stage Δv coming up right after a
// first-stage burn). Dead-zoning on epsilons could swallow the
// frame. A `_forceEmit` flag set from `onStageActivate` /
// `OnGUIStageSequenceModified` bypasses the material-change check
// on the next Update so the wire gets a clean snapshot promptly.
//
// Wire format (positional array):
//   data: [vesselId, currentStageIdx, [stage, ...]]
//   stage: [stageNum, dvActual, twrActual,
//           [[kind, persistentId, iconName, cousinsInStage], ...]]
//
//     stageNum      : int, KSP's stage numbering (lower = later);
//                     matches `DeltaVStageInfo.stage`.
//     dvActual      : m/s, at current conditions.
//     twrActual     : thrust/weight at current conditions.
//     cousinsInStage: persistentIds of the symmetry cousins that
//                     share THIS stage with the representative.
//                     Empty for singletons. Client derives the
//                     multiplicity badge ("×N") as
//                     `cousinsInStage.length + 1`, and when the
//                     user toggles "Ungroup" it renders one icon
//                     per id (representative + every entry).
//     kind          : "engine" | "decoupler" | "parachute" |
//                     "clamp" | "other". Classified by scanning
//                     the part's modules.
//     persistentId  : decimal string of `Part.persistentId`. Stable
//                     across save/load.
//     iconName      : uppercase `DefaultIcons` enum name from
//                     `Part.stagingIcon` (e.g. "LIQUID_ENGINE",
//                     "DECOUPLER_VERT", "PARACHUTES").
//
// Ordering within a stage: by kind priority (decoupler, engine,
// parachute, clamp, other) then by persistentId ascending, so the
// sequence is deterministic — material-change detection can diff
// triples positionally.

using System.Collections.Generic;
using System.Text;
using Dragonglass.Telemetry.Util;
using KSP.UI.Screens;
using UnityEngine;

namespace Dragonglass.Telemetry.Topics
{
    public class StageTopic : Topic
    {
        public override string Name { get { return "stage"; } }

        // Dead-zoning epsilons — lifted from FlightTopic's stage scalars
        // (the former CurrentStageTopic inheritance). dV under half a
        // unit and TWR under half a percent are below what the UI can
        // show meaningfully.
        private const double DvEpsilon = 0.5;
        private const float TwrEpsilon = 0.005f;

        private const string KindEngine = "engine";
        private const string KindDecoupler = "decoupler";
        private const string KindParachute = "parachute";
        private const string KindClamp = "clamp";
        private const string KindOther = "other";

        // Wire-shape DTOs. `public` so subclasses (mods that swap the
        // dV/TWR source) can populate them in their override of
        // <see cref="CollectStageScalars"/>.
        public struct StagePartFrame
        {
            public string Kind;
            public string PersistentId;
            public string IconName;
            /// <summary>
            /// persistentIds of the representative's symmetry
            /// cousins that currently share this same stage. Empty
            /// when the part is a singleton (no symmetry) or when
            /// its cousins have been scattered across other stages.
            /// The representative's own id is NOT included —
            /// `cousinsInStage.Count + 1` gives the total group
            /// size in this stage.
            /// </summary>
            public List<string> CousinsInStage;
        }

        public struct StageFrame
        {
            public int Stage;
            public double DeltaVActual;
            public float TwrActual;
            public List<StagePartFrame> Parts;
        }

        /// <summary>
        /// Scalar dV / TWR for a single stage. Populated by
        /// <see cref="CollectStageScalars"/>; the base class joins
        /// these against the parts grouping to assemble the full
        /// <see cref="StageFrame"/>s on the wire.
        /// </summary>
        public struct StageScalar
        {
            public int Stage;
            public double DeltaVActual;
            public float TwrActual;
        }

        private string _vesselId = "";
        private int _currentStageIdx = -1;
        private readonly List<StageFrame> _stages = new List<StageFrame>();

        // Structural cache — the parts list partitioning only needs to
        // be recomputed on KSP structure events (staging / docking /
        // sequence edits); per-frame we re-read dV/TWR off the same
        // cached mapping. Matches EngineTopic's pattern.
        //
        // The cache stores only immutable strings (kind, persistentId,
        // iconName) — no live Part references — so there's no
        // stale-pointer hazard if a cached part gets destroyed between
        // event and consumption.
        private bool _structureDirty = true;
        private Dictionary<int, List<StagePartFrame>> _partsByStage =
            new Dictionary<int, List<StagePartFrame>>();

        // Skip HasMaterialChange on the next Update — used by event
        // handlers that want a fresh wire frame even when scalars
        // haven't moved beyond epsilon (staging transitions, sequence
        // edits).
        private bool _forceEmit;
        private Vessel _cachedVessel;

        // Scratch list reused across frames.
        private readonly List<StageFrame> _scratch = new List<StageFrame>();
        private readonly List<StageScalar> _scalarScratch = new List<StageScalar>();

        // The set of parts currently hover-highlighted in the 3D
        // scene. Replaced wholesale on each setHighlightParts op.
        // Size is usually 0 or 1, up to N for a symmetry group
        // (quad-SRB cluster = 4).
        private readonly List<Part> _highlightedParts = new List<Part>();

        // Shared empty list for stages with no icon-worthy parts —
        // avoids allocating a fresh List<> per empty stage per frame.
        private static readonly List<StagePartFrame> EmptyParts =
            new List<StagePartFrame>(0);

        protected override void OnEnable()
        {
            base.OnEnable();
            GameEvents.onStageActivate.Add(OnStageActivate);
            GameEvents.StageManager.OnGUIStageSequenceModified.Add(OnStageSequenceModified);
            GameEvents.onDeltaVCalcsCompleted.Add(OnDeltaVCalcsCompleted);
            GameEvents.onVesselChange.Add(OnVesselChange);
            GameEvents.onVesselWasModified.Add(OnVesselWasModified);
            GameEvents.onDockingComplete.Add(OnDockingComplete);
            GameEvents.onVesselsUndocking.Add(OnUndocking);
            // Editor-side structure triggers. Parts added / deleted /
            // symmetry-cloned in the VAB/SPH all rebuild the staging
            // list, so we dirty the cache and let Update resample.
            GameEvents.onEditorShipModified.Add(OnEditorShipModified);
            GameEvents.onEditorPartEvent.Add(OnEditorPartEvent);
            GameEvents.onEditorLoad.Add(OnEditorLoad);
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            // Clear any lingering hover highlight so the parts aren't
            // left glowing when we leave the Flight scene.
            ClearHighlights();
            GameEvents.onStageActivate.Remove(OnStageActivate);
            GameEvents.StageManager.OnGUIStageSequenceModified.Remove(OnStageSequenceModified);
            GameEvents.onDeltaVCalcsCompleted.Remove(OnDeltaVCalcsCompleted);
            GameEvents.onVesselChange.Remove(OnVesselChange);
            GameEvents.onVesselWasModified.Remove(OnVesselWasModified);
            GameEvents.onDockingComplete.Remove(OnDockingComplete);
            GameEvents.onVesselsUndocking.Remove(OnUndocking);
            GameEvents.onEditorShipModified.Remove(OnEditorShipModified);
            GameEvents.onEditorPartEvent.Remove(OnEditorPartEvent);
            GameEvents.onEditorLoad.Remove(OnEditorLoad);
        }

        private void OnEditorShipModified(ShipConstruct _) { _structureDirty = true; _forceEmit = true; }
        private void OnEditorPartEvent(ConstructionEventType _, Part __) { _structureDirty = true; }
        private void OnEditorLoad(ShipConstruct _, CraftBrowserDialog.LoadType __) { _structureDirty = true; _forceEmit = true; }

        // Staging transition — force a fresh frame on the wire.
        private void OnStageActivate(int _) { _structureDirty = true; _forceEmit = true; }
        // Stage sequence edited in the stock stager — icons may have
        // moved between stages. Rebuild and force-emit.
        private void OnStageSequenceModified() { _structureDirty = true; _forceEmit = true; }
        // dV simulator finished — `OperatingStageInfo` is freshly valid.
        private void OnDeltaVCalcsCompleted() { /* next Update samples */ }
        private void OnVesselChange(Vessel _)
        {
            _structureDirty = true;
            _forceEmit = true;
            // Drop any stale highlight pointers; the referenced parts
            // may belong to the previous vessel. We don't try to
            // clear their highlights — they're already off-scene.
            _highlightedParts.Clear();
        }
        private void OnVesselWasModified(Vessel v) { if (v == FlightGlobals.ActiveVessel) _structureDirty = true; }
        private void OnDockingComplete(GameEvents.FromToAction<Part, Part> _) { _structureDirty = true; }
        private void OnUndocking(Vessel _, Vessel __) { _structureDirty = true; }

        private void Update()
        {
            // Source abstraction. Flight reads the active vessel;
            // Editor reads the in-construction ShipConstruct. Both
            // expose the same three bits we need — parts list,
            // VesselDeltaV, and an identity string — so the rest of
            // the sample pipeline is scene-agnostic.
            List<Part> parts;
            VesselDeltaV vdv;
            string nextVesselId;
            int nextCurrentStage;
            Vessel nextVessel;

            if (HighLogic.LoadedScene == GameScenes.EDITOR)
            {
                ShipConstruct ship = EditorLogic.fetch != null ? EditorLogic.fetch.ship : null;
                if (ship == null) return;
                parts = ship.parts;
                vdv = ship.vesselDeltaV;
                // ShipConstruct has no persistent id; use a stable
                // sentinel so the client knows "this is the editor
                // ship" and any flight-keyed UI state (pinned PAWs,
                // selection) resets on scene change.
                nextVesselId = "editor";
                // No active stage in the editor — report -1 so the
                // client's "current stage highlight" renders as none.
                nextCurrentStage = -1;
                nextVessel = null;
            }
            else
            {
                if (FlightGlobals.fetch == null) return;
                Vessel v = FlightGlobals.ActiveVessel;
                if (v == null) return;
                parts = v.Parts;
                vdv = v.VesselDeltaV;
                nextVesselId = v.id.ToString("D");
                nextCurrentStage = v.currentStage;
                nextVessel = v;
            }

            if (nextVessel != _cachedVessel)
            {
                _cachedVessel = nextVessel;
                _structureDirty = true;
            }

            if (_structureDirty)
            {
                _partsByStage = BuildPartsByStage(parts);
                _structureDirty = false;
            }

            _scratch.Clear();
            _scalarScratch.Clear();
            CollectStageScalars(nextVessel, vdv, _scalarScratch);
            for (int i = 0; i < _scalarScratch.Count; i++)
            {
                StageScalar sc = _scalarScratch[i];
                List<StagePartFrame> parts2;
                if (!_partsByStage.TryGetValue(sc.Stage, out parts2))
                {
                    parts2 = EmptyParts;
                }
                _scratch.Add(new StageFrame
                {
                    Stage = sc.Stage,
                    DeltaVActual = sc.DeltaVActual,
                    TwrActual = sc.TwrActual,
                    Parts = parts2,
                });
            }

            bool changed = _forceEmit
                || nextVesselId != _vesselId
                || nextCurrentStage != _currentStageIdx
                || HasMaterialChange(_stages, _scratch);

            if (changed)
            {
                _vesselId = nextVesselId;
                _currentStageIdx = nextCurrentStage;
                _stages.Clear();
                for (int i = 0; i < _scratch.Count; i++) _stages.Add(_scratch[i]);
                _forceEmit = false;
                MarkDirty();
            }
        }

        /// <summary>
        /// Populate <paramref name="scratch"/> with one
        /// <see cref="StageScalar"/> per active stage. Default impl
        /// reads <c>VesselDeltaV.OperatingStageInfo</c> — i.e. stock
        /// KSP's own dV simulator, the same numbers the stock stager
        /// shows. Override to source from a different propulsion
        /// simulation (e.g. a mod that replaces stock engines and
        /// thus sees stock dV come back as zero/garbage).
        /// <para>
        /// <paramref name="v"/> is the active <see cref="Vessel"/> in
        /// flight, or <c>null</c> in the editor scene. <paramref name="vdv"/>
        /// is the corresponding <see cref="VesselDeltaV"/> handle the
        /// stock impl uses; subclasses can ignore it.
        /// </para>
        /// </summary>
        protected virtual void CollectStageScalars(Vessel v, VesselDeltaV vdv, List<StageScalar> scratch)
        {
            List<DeltaVStageInfo> infos = vdv != null ? vdv.OperatingStageInfo : null;
            if (infos == null) return;
            for (int i = 0; i < infos.Count; i++)
            {
                DeltaVStageInfo info = infos[i];
                if (info == null) continue;
                scratch.Add(new StageScalar
                {
                    Stage = info.stage,
                    DeltaVActual = info.deltaVActual,
                    TwrActual = info.TWRActual,
                });
            }
        }

        // Build a stage→parts dictionary for the vessel's icon-worthy
        // parts, pre-sorted into presentation order (kind priority,
        // then persistentId ascending).
        //
        // Symmetry dedupe — per stage. For a cluster of physically-
        // symmetric parts that all share the SAME inverseStage, we
        // emit a single "representative" icon (the cousin with the
        // smallest persistentId) plus the other cousins'
        // persistentIds in `CousinsInStage`. The client renders ×N
        // by default and can expand to N individual icons when the
        // user toggles Ungroup.
        //
        // Critically, the dedupe is scoped per-stage: cousins that
        // have been moved to different stages (via an individual
        // move while ungrouped) are each the representative of
        // their own singleton in their respective stage. Without
        // this scoping, a cousin that's been shuffled elsewhere
        // would never appear in the UI.
        private static Dictionary<int, List<StagePartFrame>> BuildPartsByStage(List<Part> parts)
        {
            Dictionary<int, List<StagePartFrame>> byStage =
                new Dictionary<int, List<StagePartFrame>>();

            for (int i = 0; i < parts.Count; i++)
            {
                Part p = parts[i];
                if (p == null) continue;
                if (!p.hasStagingIcon) continue;
                if (!p.stagingOn) continue;
                if (!IsRepresentativeForStage(p)) continue;

                List<string> cousins = BuildCousinsInStage(p);

                List<StagePartFrame> bucket;
                if (!byStage.TryGetValue(p.inverseStage, out bucket))
                {
                    bucket = new List<StagePartFrame>();
                    byStage[p.inverseStage] = bucket;
                }
                bucket.Add(new StagePartFrame
                {
                    Kind = ClassifyKind(p),
                    PersistentId = p.persistentId.ToString(),
                    IconName = p.stagingIcon,
                    CousinsInStage = cousins,
                });
            }

            foreach (List<StagePartFrame> bucket in byStage.Values)
            {
                bucket.Sort(ComparePresentation);
            }
            return byStage;
        }

        // True iff `p` should be the wire-emitted representative for
        // its stage. Criterion: minimum persistentId among symmetry
        // cousins currently sharing the SAME inverseStage (cousins
        // elsewhere don't compete — they'll be reps of their own
        // stages). Works even when the UI-layer `groupLead` pointer
        // isn't populated, since we derive purely from vessel state.
        private static bool IsRepresentativeForStage(Part p)
        {
            List<Part> cousins = p.symmetryCounterparts;
            if (cousins == null || cousins.Count == 0) return true;
            uint minId = p.persistentId;
            int stage = p.inverseStage;
            for (int i = 0; i < cousins.Count; i++)
            {
                Part c = cousins[i];
                if (c == null) continue;
                if (c.inverseStage != stage) continue;
                if (c.persistentId < minId) return false;
            }
            return true;
        }

        // Collect persistentIds of `p`'s cousins currently at the
        // same stage. Sorted ascending so the wire order is stable
        // across rebuilds.
        private static List<string> BuildCousinsInStage(Part p)
        {
            List<Part> cousins = p.symmetryCounterparts;
            if (cousins == null || cousins.Count == 0) return EmptyCousinIds;
            int stage = p.inverseStage;
            List<uint> ids = new List<uint>();
            for (int i = 0; i < cousins.Count; i++)
            {
                Part c = cousins[i];
                if (c == null) continue;
                if (c.inverseStage != stage) continue;
                ids.Add(c.persistentId);
            }
            if (ids.Count == 0) return EmptyCousinIds;
            ids.Sort();
            List<string> result = new List<string>(ids.Count);
            for (int i = 0; i < ids.Count; i++) result.Add(ids[i].ToString());
            return result;
        }

        private static readonly List<string> EmptyCousinIds = new List<string>(0);

        private static int KindPriority(string kind)
        {
            if (kind == KindDecoupler) return 0;
            if (kind == KindEngine) return 1;
            if (kind == KindParachute) return 2;
            if (kind == KindClamp) return 3;
            return 4; // other
        }

        private static int ComparePresentation(StagePartFrame a, StagePartFrame b)
        {
            int pa = KindPriority(a.Kind);
            int pb = KindPriority(b.Kind);
            if (pa != pb) return pa - pb;
            // persistentId is decimal text of a uint. Compare
            // numerically so "10" sorts after "9" (string compare
            // would reverse them).
            ulong ia, ib;
            ulong.TryParse(a.PersistentId, out ia);
            ulong.TryParse(b.PersistentId, out ib);
            return ia < ib ? -1 : (ia > ib ? 1 : 0);
        }

        // Classify by walking the part's modules. A part that is both
        // an engine and a decoupler (SRB-style separatrons count as
        // engines, not decouplers — stock's stagingIcon already
        // reflects that) gets the engine classification first;
        // ModuleDecouplerBase covers both stock decoupler modules.
        private static string ClassifyKind(Part p)
        {
            if (p.FindModuleImplementing<ModuleEngines>() != null) return KindEngine;
            if (p.FindModuleImplementing<ModuleDecouplerBase>() != null) return KindDecoupler;
            if (p.FindModuleImplementing<ModuleParachute>() != null) return KindParachute;
            if (p.FindModuleImplementing<LaunchClamp>() != null) return KindClamp;
            // ModuleDockingNode counts as a decoupler only when its
            // staging mode is enabled — the port can be configured
            // either way and only the enabled-for-staging case shows
            // up in the stager with a separation action.
            ModuleDockingNode dock = p.FindModuleImplementing<ModuleDockingNode>();
            if (dock != null && dock.StagingEnabled()) return KindDecoupler;
            return KindOther;
        }

        private static bool CousinListEqual(List<string> a, List<string> b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return (a == null) == (b == null);
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static bool HasMaterialChange(
            List<StageFrame> prev, List<StageFrame> next)
        {
            if (prev.Count != next.Count) return true;
            for (int i = 0; i < next.Count; i++)
            {
                StageFrame a = prev[i];
                StageFrame b = next[i];
                if (a.Stage != b.Stage) return true;
                if (System.Math.Abs(a.DeltaVActual - b.DeltaVActual) > DvEpsilon) return true;
                if (Mathf.Abs(a.TwrActual - b.TwrActual) > TwrEpsilon) return true;
                if (a.Parts.Count != b.Parts.Count) return true;
                for (int j = 0; j < b.Parts.Count; j++)
                {
                    StagePartFrame pa = a.Parts[j];
                    StagePartFrame pb = b.Parts[j];
                    if (pa.Kind != pb.Kind) return true;
                    if (pa.PersistentId != pb.PersistentId) return true;
                    if (pa.IconName != pb.IconName) return true;
                    if (!CousinListEqual(pa.CousinsInStage, pb.CousinsInStage)) return true;
                }
            }
            return false;
        }

        // ---- Op handlers ----------------------------------------
        //
        // Inbound editing ops from the UI. The wire shape follows
        // OpDispatcher's conventions: JSON numbers arrive as double
        // (cast to int), strings as string. Every successful mutation
        // sets `_forceEmit` so the next broadcaster tick ships a
        // fresh frame, and fires `GameEvents.StageManager.OnGUIStageSequenceModified`
        // (plus `OnGUIStageAdded` / `OnGUIStageRemoved` where
        // applicable) so stock KSP's own stager UI stays in sync.
        //
        // All ops run on Unity's main thread via OpDispatcher.Drain,
        // so direct calls into StageManager / Part fields are safe.

        public override void HandleOp(string op, List<object> args)
        {
            switch (op)
            {
                case "movePart":
                    // args: [persistentId, targetStageNum, group]
                    if (TryArgString(args, 0, out string mpId)
                        && TryArgInt(args, 1, out int mpTarget))
                    {
                        bool mpGroup = false;
                        TryArgBool(args, 2, out mpGroup);
                        DoMovePart(mpId, mpTarget, mpGroup);
                    }
                    break;
                case "movePartToNewStage":
                    // args: [persistentId, position, group]
                    if (TryArgString(args, 0, out string mnId)
                        && TryArgString(args, 1, out string mnPos))
                    {
                        bool mnGroup = false;
                        TryArgBool(args, 2, out mnGroup);
                        DoMovePartToNewStage(mnId, mnPos, mnGroup);
                    }
                    break;
                case "moveStage":
                    // args: [fromStageNum, insertPos]
                    if (TryArgInt(args, 0, out int msFrom)
                        && TryArgInt(args, 1, out int msInsert))
                    {
                        DoMoveStage(msFrom, msInsert);
                    }
                    break;
                case "setHighlightParts":
                    {
                        // args: [[persistentId, ...]]  — a single
                        // positional array of ids. An empty list
                        // clears any current highlight.
                        List<string> hlIds = null;
                        if (args != null && args.Count >= 1 && args[0] is List<object> raw)
                        {
                            hlIds = new List<string>(raw.Count);
                            for (int i = 0; i < raw.Count; i++)
                            {
                                if (raw[i] is string s) hlIds.Add(s);
                            }
                        }
                        DoSetHighlightParts(hlIds);
                    }
                    break;
                default:
                    Debug.LogWarning("[Dragonglass/Telemetry] StageTopic: " +
                                     "unknown op '" + op + "'");
                    break;
            }
        }

        // Move a single part into an existing stage by replicating
        // stock KSP's drag-and-drop OnEndDrag sequence (see
        // StageIcon.OnEndDrag in ksp-reference) minus the symmetry /
        // group-lead handling — we move one part at a time.
        //
        // `AddIconAt` internally calls `icon.SetInverseSequenceIndex`
        // which rewrites `part.inverseStage` to match the target
        // group's index, so we don't set it manually.
        // Move a single part (or its whole symmetry group in-stage)
        // into an existing stage.
        //
        // `group=true` propagates the move to every cousin currently
        // sharing the source stage — used when the user drags / right-
        // clicks the consolidated "×N" icon and expects the whole
        // group to ride together. `group=false` moves only the named
        // part (its cousins stay in the source stage), which is the
        // behaviour after the user has clicked "Ungroup" in the UI
        // and is manipulating cousins individually.
        //
        // Implementation: direct `part.inverseStage` mutation rather
        // than going through stock's icon plumbing. Stock's
        // `AddIconAt`→`SetInverseSequenceIndex` auto-propagates to
        // `groupedIcons` when the icon is a group lead, which we
        // can't reliably control (who's the lead is set at vessel
        // load, not by us). Bypassing gives precise control but means
        // stock's stager icon rows fall out of sync with part state —
        // acceptable because we're hiding the stock stager anyway.
        private void DoMovePart(string persistentId, int targetStageNum, bool group)
        {
            List<Part> parts = CurrentScenePartsList();
            if (parts == null) return;
            StageManager sm = StageManager.Instance;
            if (sm == null || sm.Stages == null) return;
            if (targetStageNum < 0 || targetStageNum >= sm.Stages.Count) return;

            Part part = FindPart(parts, persistentId);
            if (part == null) return;

            int sourceStage = part.inverseStage;
            if (sourceStage == targetStageNum) return;

            SetPartStageDirect(part, targetStageNum);
            if (group && part.symmetryCounterparts != null)
            {
                for (int i = 0; i < part.symmetryCounterparts.Count; i++)
                {
                    Part cousin = part.symmetryCounterparts[i];
                    if (cousin != null && cousin != part && cousin.inverseStage == sourceStage)
                    {
                        SetPartStageDirect(cousin, targetStageNum);
                    }
                }
            }

            // Collapse the source stage if nothing icon-worthy lives
            // on it anymore — keeps the stage list tight without
            // orphan rows.
            if (sourceStage >= 0 && sourceStage < sm.Stages.Count
                && !IsStageInhabited(parts, sourceStage))
            {
                CollapseEmptyStage(sm, sm.Stages[sourceStage]);
            }
            else
            {
                StageManager.SetSeparationIndices();
            }

            _forceEmit = true;
            GameEvents.StageManager.OnGUIStageSequenceModified.Fire();
        }

        // Insert a new stage adjacent to the part's current one and
        // move the part (or its group) into it. `above` = lower
        // stageNum in our UI (inserts at source's list position,
        // shifting source up); `below` = higher stageNum (inserts at
        // source+1, source stays put).
        private void DoMovePartToNewStage(string persistentId, string position, bool group)
        {
            List<Part> parts = CurrentScenePartsList();
            if (parts == null) return;
            StageManager sm = StageManager.Instance;
            if (sm == null || sm.Stages == null) return;

            Part part = FindPart(parts, persistentId);
            if (part == null) return;

            int sourceStage = part.inverseStage;
            if (sourceStage < 0 || sourceStage >= sm.Stages.Count) return;

            int insertAt;
            if (position == "below") insertAt = sourceStage + 1;
            else if (position == "above") insertAt = sourceStage;
            else return;

            InsertStageAndMovePart(sm, parts, part, insertAt, group);
        }

        // Insert an empty stage at list position `insertAt`, then
        // move the given `part` (and optionally its symmetry cousins
        // in the same source stage) into it. Uses stock's own
        // `StageGroup.AddStageAfter` on the previous group whenever
        // possible so all the `IncrementCurrentStage` /
        // `SetManualStageOffset` / `OnGUIStageAdded` bookkeeping runs
        // through stock's proven path; falls back to a manual
        // `AddStageAt(0)` for the "insert at the very top" case.
        //
        // After the insert, stages that previously lived at index
        // >= insertAt have shifted up by one. Any part whose
        // inverseStage was >= insertAt got its field rewritten by
        // stock's `UpdateStageGroups` (via `SetPartIndices` →
        // `SetInverseSequenceIndex`). So `part.inverseStage` may now
        // be `sourceStage + 1` if sourceStage was >= insertAt, and we
        // resample it before doing the move.
        private void InsertStageAndMovePart(
            StageManager sm, List<Part> parts, Part part, int insertAt, bool group)
        {
            int sourceStage = part.inverseStage;

            if (insertAt > 0 && insertAt <= sm.Stages.Count)
            {
                StageGroup anchor = sm.Stages[insertAt - 1];
                if (anchor == null) return;
                anchor.AddStageAfter();
            }
            else if (insertAt == 0)
            {
                sm.IncrementCurrentStage();
                sm.AddStageAt(0);
                sm.SetManualStageOffset(0);
                StageManager.SetSeparationIndices();
                GameEvents.StageManager.OnGUIStageAdded.Fire(0);
            }
            else
            {
                return;
            }

            if (insertAt >= sm.Stages.Count) return;

            // Resample sourceStage — stock's UpdateStageGroups inside
            // AddStageAt/AddStageAfter may have shifted it up by one.
            int newSourceStage = part.inverseStage;

            SetPartStageDirect(part, insertAt);
            if (group && part.symmetryCounterparts != null)
            {
                for (int i = 0; i < part.symmetryCounterparts.Count; i++)
                {
                    Part cousin = part.symmetryCounterparts[i];
                    if (cousin != null && cousin != part
                        && cousin.inverseStage == newSourceStage)
                    {
                        SetPartStageDirect(cousin, insertAt);
                    }
                }
            }

            if (newSourceStage >= 0 && newSourceStage < sm.Stages.Count
                && !IsStageInhabited(parts, newSourceStage))
            {
                CollapseEmptyStage(sm, sm.Stages[newSourceStage]);
            }
            else
            {
                StageManager.SetSeparationIndices();
            }

            _forceEmit = true;
            GameEvents.StageManager.OnGUIStageSequenceModified.Fire();
        }

        // Reorder a whole stage — take every part currently at
        // stageNum `from` and move it to insertion position `insertPos`
        // in [0, stages.Count]. Other stages shift around it:
        //   - If insertPos > from: stages in (from, insertPos) shift
        //     down by one (their inverseStage decreases).
        //   - If insertPos <= from: stages in [insertPos, from) shift
        //     up by one (their inverseStage increases).
        // The stage's new stageNum is `insertPos - 1` when
        // `insertPos > from` (removing `from` first shifts the
        // insertion index by one), else `insertPos`.
        //
        // Pre-launch scenario is the target use case; mid-flight
        // reorders are legal but don't touch `_currentStage` since
        // the numeric pointer stays valid — whichever parts now live
        // at `currentStage - 1` fire on the next space press.
        private void DoMoveStage(int fromStageNum, int insertPos)
        {
            List<Part> parts = CurrentScenePartsList();
            if (parts == null) return;
            StageManager sm = StageManager.Instance;
            if (sm == null || sm.Stages == null) return;
            if (fromStageNum < 0 || fromStageNum >= sm.Stages.Count) return;
            if (insertPos < 0 || insertPos > sm.Stages.Count) return;
            if (insertPos == fromStageNum || insertPos == fromStageNum + 1) return;

            int newFromStage = insertPos > fromStageNum ? insertPos - 1 : insertPos;

            for (int i = 0; i < parts.Count; i++)
            {
                Part p = parts[i];
                if (p == null) continue;
                int old = p.inverseStage;
                int next;
                if (old == fromStageNum)
                {
                    next = newFromStage;
                }
                else if (fromStageNum < insertPos && old > fromStageNum && old < insertPos)
                {
                    next = old - 1;
                }
                else if (fromStageNum > insertPos && old >= insertPos && old < fromStageNum)
                {
                    next = old + 1;
                }
                else
                {
                    continue;
                }
                SetPartStageDirect(p, next);
            }

            StageManager.SetSeparationIndices();
            _forceEmit = true;
            GameEvents.StageManager.OnGUIStageSequenceModified.Fire();
        }

        // True iff any part with a staging icon is currently assigned
        // to `stageNum`. Used to decide whether the just-emptied
        // source stage should collapse.
        private static bool IsStageInhabited(List<Part> parts, int stageNum)
        {
            for (int i = 0; i < parts.Count; i++)
            {
                Part p = parts[i];
                if (p == null) continue;
                if (!p.hasStagingIcon) continue;
                if (!p.stagingOn) continue;
                if (p.inverseStage == stageNum) return true;
            }
            return false;
        }

        // The current scene's parts list — flight's active vessel, or
        // the editor's in-construction ship. Returns null when neither
        // is available (not in Flight or Editor, or the scene has no
        // vessel yet), which the mutation handlers treat as "drop".
        private static List<Part> CurrentScenePartsList()
        {
            if (HighLogic.LoadedScene == GameScenes.EDITOR)
            {
                ShipConstruct ship = EditorLogic.fetch != null ? EditorLogic.fetch.ship : null;
                return ship != null ? ship.parts : null;
            }
            Vessel v = FlightGlobals.fetch != null ? FlightGlobals.ActiveVessel : null;
            return v != null ? v.Parts : null;
        }

        // Direct-field update of a part's staging assignment.
        // Mirrors stock's `StageIcon.SetInverseSequenceIndex` (minus
        // the symmetry-group propagation): inverseStage +
        // manualStageOffset always, and separationIndex only for
        // parts that actually fire on staging (engines + decouplers).
        private static void SetPartStageDirect(Part part, int stageNum)
        {
            if (part == null) return;
            part.inverseStage = stageNum;
            part.manualStageOffset = stageNum;
            if (part.isEngine()
                || part.FindModuleImplementing<ModuleDecouplerBase>() != null)
            {
                part.separationIndex = stageNum;
            }
        }

        // Remove `group` (which must be empty) from the stage list
        // and fix up the surrounding bookkeeping. Mirrors
        // StageGroup.Delete (ksp-reference StageGroup.cs:715) but
        // without the editor-selection guards — we're always in
        // flight when this fires.
        private static void CollapseEmptyStage(StageManager sm, StageGroup group)
        {
            if (group == null) return;
            int idx = sm.Stages.IndexOf(group);
            if (idx < 0) return;

            sm.DecrementCurrentStage();
            sm.DeleteStage(group, tweenOut: false);
            sm.SetManualStageOffset(idx);
            StageManager.SetSeparationIndices();
            GameEvents.StageManager.OnGUIStageRemoved.Fire(idx);
        }

        // Hover-to-highlight. Mirrors stock's StageIcon.HighlightPart
        // (`Part.SetHighlight(state, recursive: false)`) but over a
        // set of parts so hovering a consolidated symmetry icon can
        // glow every cousin in the group simultaneously. The client
        // sends the full set on each enter / leave — latest call
        // wins — so we don't have to track "what should remain lit
        // after this change" ourselves; we just replace.
        private void DoSetHighlightParts(List<string> persistentIds)
        {
            ClearHighlights();
            if (persistentIds == null || persistentIds.Count == 0) return;

            List<Part> parts = CurrentScenePartsList();
            if (parts == null) return;
            for (int i = 0; i < persistentIds.Count; i++)
            {
                Part p = FindPart(parts, persistentIds[i]);
                if (p == null) continue;
                p.SetHighlight(true, recursive: false);
                _highlightedParts.Add(p);
            }
        }

        private void ClearHighlights()
        {
            for (int i = 0; i < _highlightedParts.Count; i++)
            {
                Part p = _highlightedParts[i];
                if (p == null) continue;
                try { p.SetHighlight(false, recursive: false); }
                catch { /* part may already be destroyed — fine */ }
            }
            _highlightedParts.Clear();
        }

        private static Part FindPart(List<Part> parts, string persistentId)
        {
            if (parts == null || string.IsNullOrEmpty(persistentId)) return null;
            uint id;
            if (!uint.TryParse(persistentId, out id)) return null;
            for (int i = 0; i < parts.Count; i++)
            {
                Part p = parts[i];
                if (p != null && p.persistentId == id) return p;
            }
            return null;
        }

        private static bool TryArgInt(List<object> args, int index, out int value)
        {
            value = 0;
            if (args == null || index >= args.Count) return false;
            // JSON numbers arrive as double from Json.Parse.
            if (args[index] is double d) { value = (int)d; return true; }
            return false;
        }

        private static bool TryArgBool(List<object> args, int index, out bool value)
        {
            value = false;
            if (args == null || index >= args.Count) return false;
            if (args[index] is bool b) { value = b; return true; }
            return false;
        }

        private static bool TryArgString(List<object> args, int index, out string value)
        {
            value = null;
            if (args == null || index >= args.Count) return false;
            if (args[index] is string s) { value = s; return true; }
            return false;
        }

        public override void WriteData(StringBuilder sb)
        {
            sb.Append('[');
            Json.WriteString(sb, _vesselId);
            sb.Append(',');
            Json.WriteLong(sb, _currentStageIdx);
            sb.Append(',');
            sb.Append('[');
            for (int i = 0; i < _stages.Count; i++)
            {
                if (i > 0) sb.Append(',');
                StageFrame sf = _stages[i];
                sb.Append('[');
                Json.WriteLong(sb, sf.Stage);
                sb.Append(',');
                Json.WriteDouble(sb, sf.DeltaVActual);
                sb.Append(',');
                Json.WriteFloat(sb, sf.TwrActual);
                sb.Append(',');
                sb.Append('[');
                for (int j = 0; j < sf.Parts.Count; j++)
                {
                    if (j > 0) sb.Append(',');
                    StagePartFrame pf = sf.Parts[j];
                    sb.Append('[');
                    Json.WriteString(sb, pf.Kind);
                    sb.Append(',');
                    Json.WriteString(sb, pf.PersistentId);
                    sb.Append(',');
                    Json.WriteString(sb, pf.IconName);
                    sb.Append(',');
                    sb.Append('[');
                    List<string> cousins = pf.CousinsInStage;
                    if (cousins != null)
                    {
                        for (int k = 0; k < cousins.Count; k++)
                        {
                            if (k > 0) sb.Append(',');
                            Json.WriteString(sb, cousins[k]);
                        }
                    }
                    sb.Append(']');
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
