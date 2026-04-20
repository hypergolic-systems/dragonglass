// Fuel-group partitioning. Takes the raw per-engine frame from
// `EngineTopic` and emits the `EngineGroup[]` the Propulsion panel
// renders — one row per distinct fuel pool, with pooled totals.
//
// Two engines belong to the same fuel group iff they draw from the
// same crossfeed-connected part set for the same propellants. That's
// the "structural" signature — it's computed from the server-sent
// `crossfeedPartIds` + `propellants` and is stable while the vessel
// is rigid (changes only on staging, docking, flow toggles).
//
// We additionally *merge* groups that are structurally distinct but
// indistinguishable to the pilot — same engine count + same
// per-engine max thrust multiset + same propellants at matching
// totals. That collapses e.g. two symmetric booster stacks into a
// single gauge row that tracks them in lockstep.
//
// Within a **structure epoch** (the bucketing output is unchanged
// from the previous call), we never re-merge — only split. If a
// merged aggregate's pre-merge constituents have drifted past the
// threshold, the drifter gets kicked out as its own group. Monotonic
// splitting prevents gauge rows from flickering at the merge-
// threshold boundary as fuel burns. On any structural change
// (engines added, removed, or their signature changes), the state
// resets and merging runs fresh.
//
// This module is stateful — it owns a module-scoped epoch record so
// it can distinguish "continuing the same epoch" from "new epoch".
// There's one HUD instance per CEF process; that single instance is
// the only caller.

import type {
  EnginePoint,
  EnginePropellant,
  EngineGroup,
} from '@dragonglass/telemetry/core';

// Merge threshold: per-propellant amount + capacity must match
// within this fractional delta for two components to stay merged.
// Matches the old server-side `MergeFuelThreshold`.
const MERGE_FUEL_THRESHOLD = 0.001;

// One pre-merge bucket from pass 1 of the algorithm. Identity
// (signature + engine IDs) is stable across frames within an epoch;
// propellant totals are refreshed every frame from the latest
// `EnginePoint` values.
interface Component {
  readonly signature: string;
  readonly engineIds: readonly string[];
  readonly maxThrusts: readonly number[];
  propellants: readonly EnginePropellant[];
}

interface Group {
  components: Component[];
}

let currentGroups: Group[] = [];
let lastEpochKey: string | null = null;

/**
 * Partition the engines into fuel groups. Call every frame; the
 * module maintains its own state across calls to handle the
 * merge-monotonic split behaviour.
 */
export function groupEngines(engines: readonly EnginePoint[]): EngineGroup[] {
  const components = bucketBySignature(engines);
  const epochKey = computeEpochKey(components);

  if (epochKey !== lastEpochKey) {
    // Fresh epoch: seed one group per component, then run merge
    // pass to a fixpoint.
    currentGroups = components.map((c) => ({ components: [c] }));
    mergeEquivalentGroups(currentGroups);
    lastEpochKey = epochKey;
  } else {
    // Same epoch: update the existing groups' components in place
    // (component signatures are stable, so we can look them up),
    // then run the drift-split pass.
    refreshComponents(currentGroups, components);
    splitDriftedGroups(currentGroups);
  }

  return currentGroups.map(materializeGroup);
}

// ---- Pass 1: bucket engines by structural signature ---------

function bucketBySignature(engines: readonly EnginePoint[]): Component[] {
  const byKey = new Map<string, EnginePoint[]>();
  for (const eng of engines) {
    const key = signatureOf(eng);
    let bucket = byKey.get(key);
    if (!bucket) {
      bucket = [];
      byKey.set(key, bucket);
    }
    bucket.push(eng);
  }

  const out: Component[] = [];
  for (const [sig, bucket] of byKey) {
    // Any engine in the bucket has authoritative propellant totals
    // — they all share the same crossfeed reach, so their
    // amount/capacity per propellant is identical by construction.
    // Sort engine IDs for stable identity across insertion order.
    const engineIds = bucket.map((e) => e.id).sort();
    const maxThrusts = bucket.map((e) => e.maxThrust).sort((a, b) => a - b);
    out.push({
      signature: sig,
      engineIds,
      maxThrusts,
      propellants: bucket[0].propellants,
    });
  }
  return out;
}

function signatureOf(engine: EnginePoint): string {
  const crossfeed = [...engine.crossfeedPartIds].sort().join(',');
  const props = engine.propellants.map((p) => p.resourceName).sort().join(',');
  return `${crossfeed}|${props}`;
}

// Epoch key = all component signatures + their engine sets, sorted.
// Changes iff engines are added, removed, or their crossfeed /
// propellant structure changed.
function computeEpochKey(components: readonly Component[]): string {
  const parts = components.map((c) => `${c.signature}=${c.engineIds.join(',')}`);
  parts.sort();
  return parts.join(';');
}

// ---- Pass 2: merge equivalent groups (new epoch only) -------

function mergeEquivalentGroups(groups: Group[]): void {
  let mergedAny = true;
  while (mergedAny && groups.length > 1) {
    mergedAny = false;
    outer: for (let i = 0; i < groups.length; i++) {
      for (let j = i + 1; j < groups.length; j++) {
        if (canMerge(groups[i], groups[j])) {
          groups[i] = {
            components: [...groups[i].components, ...groups[j].components],
          };
          groups.splice(j, 1);
          mergedAny = true;
          break outer;
        }
      }
    }
  }
}

function canMerge(a: Group, b: Group): boolean {
  // Treat each group as a single aggregated component for the merge
  // check — sum each group's components' propellant totals, compare
  // "engine type multisets" (proxied by maxThrust), and require the
  // propellant sets to match by name.
  const aAgg = aggregate(a);
  const bAgg = aggregate(b);

  if (aAgg.maxThrusts.length !== bAgg.maxThrusts.length) return false;
  for (let i = 0; i < aAgg.maxThrusts.length; i++) {
    if (aAgg.maxThrusts[i] !== bAgg.maxThrusts[i]) return false;
  }

  if (aAgg.propellants.length !== bAgg.propellants.length) return false;
  for (let i = 0; i < aAgg.propellants.length; i++) {
    const pa = aAgg.propellants[i];
    const pb = bAgg.propellants[i];
    if (pa.resourceName !== pb.resourceName) return false;
    const scale = Math.max(pa.capacity, pb.capacity);
    if (scale <= 1e-9) continue;
    if (Math.abs(pa.available - pb.available) / scale > MERGE_FUEL_THRESHOLD) return false;
    if (Math.abs(pa.capacity - pb.capacity) / scale > MERGE_FUEL_THRESHOLD) return false;
  }
  return true;
}

// ---- Pass 3: drift-split (same epoch only) ------------------

// Refresh each component's propellant totals from the fresh bucket
// output, keyed by component signature. Every current component has
// a corresponding fresh bucket because the epoch key is unchanged.
function refreshComponents(
  groups: readonly Group[],
  freshComponents: readonly Component[],
): void {
  const bySig = new Map<string, Component>();
  for (const c of freshComponents) bySig.set(c.signature, c);
  for (const g of groups) {
    for (const c of g.components) {
      const fresh = bySig.get(c.signature);
      if (fresh) c.propellants = fresh.propellants;
    }
  }
}

function splitDriftedGroups(groups: Group[]): void {
  for (let g = groups.length - 1; g >= 0; g--) {
    const comps = groups[g].components;
    if (comps.length < 2) continue;
    const norm = comps[0];
    const kept: Component[] = [norm];
    const kicked: Component[] = [];
    for (let i = 1; i < comps.length; i++) {
      if (hasDrifted(norm, comps[i])) kicked.push(comps[i]);
      else kept.push(comps[i]);
    }
    if (kicked.length === 0) continue;
    groups[g] = { components: kept };
    groups.splice(g + 1, 0, { components: kicked });
  }
}

function hasDrifted(a: Component, b: Component): boolean {
  if (a.propellants.length !== b.propellants.length) return true;
  for (let i = 0; i < a.propellants.length; i++) {
    const pa = a.propellants[i];
    const pb = b.propellants[i];
    if (pa.resourceName !== pb.resourceName) return true;
    const scale = Math.max(pa.capacity, pb.capacity);
    if (scale <= 1e-9) continue;
    if (Math.abs(pa.available - pb.available) / scale > MERGE_FUEL_THRESHOLD) return true;
    if (Math.abs(pa.capacity - pb.capacity) / scale > MERGE_FUEL_THRESHOLD) return true;
  }
  return false;
}

// ---- Output ------------------------------------------------

interface MutableFuel {
  resourceName: string;
  abbr: string;
  available: number;
  capacity: number;
}

interface Aggregate {
  readonly engineIds: string[];
  readonly maxThrusts: number[];
  readonly propellants: EnginePropellant[];
}

function aggregate(group: Group): Aggregate {
  const engineIds: string[] = [];
  const maxThrusts: number[] = [];
  // Sum propellant totals component-wise. Every component in a
  // group carries the same propellant set in the same order
  // (enforced by bucketing + merge checks), so we can index into
  // the first component's shape and add contributions from the rest.
  const first = group.components[0];
  const propellants: MutableFuel[] = first.propellants.map((p) => ({
    resourceName: p.resourceName,
    abbr: p.abbr,
    available: 0,
    capacity: 0,
  }));
  for (const c of group.components) {
    for (const id of c.engineIds) engineIds.push(id);
    for (const t of c.maxThrusts) maxThrusts.push(t);
    for (let i = 0; i < c.propellants.length; i++) {
      propellants[i].available += c.propellants[i].available;
      propellants[i].capacity += c.propellants[i].capacity;
    }
  }
  maxThrusts.sort((a, b) => a - b);
  return { engineIds, maxThrusts, propellants };
}

function materializeGroup(group: Group): EngineGroup {
  const agg = aggregate(group);
  return {
    engineIds: agg.engineIds,
    propellants: agg.propellants,
  };
}

// Exposed for tests.
export function _resetStateForTesting(): void {
  currentGroups = [];
  lastEpochKey = null;
}
