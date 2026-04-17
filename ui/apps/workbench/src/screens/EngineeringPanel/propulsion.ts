// Pure derivations over the assembly model for the Propulsion subsystem.
//
// ΔV / TWR / burn time are computed on the "physical structure at this
// moment" — the vessels in scope, only enabled engines contributing thrust,
// and the stack's combined wet/dry mass. That gives a well-defined answer
// whether you're looking at a single vessel or a docked assembly, and it
// matches the "reboost ΔV" question station operators care about.

import {
  PROPELLANT_DENSITY,
  RESOURCE_DISPLAY_NAME,
  type AssemblyModel,
  type PartModel,
  type ResourceId,
  type VesselModel,
} from '@dragonglass/telemetry/core';
import type { Category, CategoryId, Scope, TaggedPart } from './power';

const G0 = 9.81;

export type PropulsionCategoryId = Extract<
  CategoryId,
  'engines' | 'rcs' | 'propellant'
>;

const PROPULSION_CATEGORY_ORDER: PropulsionCategoryId[] = [
  'engines',
  'rcs',
  'propellant',
];

const PROPULSION_CATEGORY_LABELS: Record<PropulsionCategoryId, string> = {
  engines: 'ENGINES',
  rcs: 'RCS',
  propellant: 'PROPELLANT',
};

function propulsionCategoryOf(p: PartModel): PropulsionCategoryId | null {
  if (p.kind === 'engine') return 'engines';
  if (p.kind === 'rcs') return 'rcs';
  if (p.kind === 'tank') return 'propellant';
  // Pods with built-in monoprop show up as propellant for the Propulsion lens.
  if (p.kind === 'pod' && p.tanks) return 'propellant';
  return null;
}

export function categorizePropulsion(
  assembly: AssemblyModel,
  scope: Scope,
): Category[] {
  const vessels: VesselModel[] =
    scope.kind === 'assembly'
      ? assembly.vessels
      : [assembly.vessels.find((v) => v.id === scope.id)!];

  const buckets: Record<PropulsionCategoryId, TaggedPart[]> = {
    engines: [],
    rcs: [],
    propellant: [],
  };

  for (const vessel of vessels) {
    for (const part of vessel.parts) {
      const cat = propulsionCategoryOf(part);
      if (!cat) continue;
      buckets[cat].push({ part, vessel });
    }
  }

  return PROPULSION_CATEGORY_ORDER.map<Category>((id) => ({
    id,
    label: PROPULSION_CATEGORY_LABELS[id],
    items: buckets[id],
    summary: { count: buckets[id].length },
  }));
}

/* ---------------- Sub-accordion categorizers ---------------- */

function scopedVessels(assembly: AssemblyModel, scope: Scope): VesselModel[] {
  return scope.kind === 'assembly'
    ? assembly.vessels
    : [assembly.vessels.find((v) => v.id === scope.id)!];
}

/**
 * Tankage: one category per propellant resource type (RP1, LOX, HYDRAZINE).
 * Multi-resource tanks appear in multiple categories so each resource's
 * mass and total can be read at a glance.
 */
export function categorizeTankage(
  assembly: AssemblyModel,
  scope: Scope,
): Category[] {
  const vessels = scopedVessels(assembly, scope);
  // Preserve a canonical ordering so the accordion doesn't reshuffle.
  const order: ResourceId[] = ['LF', 'Ox', 'Mono'];
  const buckets: Partial<Record<ResourceId, TaggedPart[]>> = {};
  for (const v of vessels) {
    for (const part of v.parts) {
      if (!part.tanks) continue;
      for (const key of Object.keys(part.tanks) as ResourceId[]) {
        if (!part.tanks[key]) continue;
        (buckets[key] ??= []).push({ part, vessel: v });
      }
    }
  }
  const out: Category[] = [];
  for (const resource of order) {
    const items = buckets[resource];
    if (!items || items.length === 0) continue;
    out.push({
      id: 'propellant',
      label: RESOURCE_DISPLAY_NAME[resource],
      items,
      summary: { count: items.length },
    });
  }
  return out;
}

/**
 * Recipe key for grouping engines: sorted propellant codes joined with +.
 * e.g. LV-909 → "LF+Ox".
 */
function recipeKey(e: { propellants: ResourceId[] }): string {
  return [...e.propellants].sort().join('+');
}

function recipeDisplayLabel(e: { propellants: ResourceId[] }): string {
  return [...e.propellants]
    .sort()
    .map((r) => RESOURCE_DISPLAY_NAME[r])
    .join(' + ');
}

/**
 * Engines grouped by fuel recipe. Each recipe is a Category with items =
 * engines using that recipe. One group per distinct propellant combination.
 */
export function categorizeEngines(
  assembly: AssemblyModel,
  scope: Scope,
): Category[] {
  const vessels = scopedVessels(assembly, scope);
  const buckets = new Map<string, { label: string; items: TaggedPart[] }>();
  for (const v of vessels) {
    for (const part of v.parts) {
      if (part.kind !== 'engine' || !part.engine) continue;
      const key = recipeKey(part.engine);
      if (!buckets.has(key)) {
        buckets.set(key, { label: recipeDisplayLabel(part.engine), items: [] });
      }
      buckets.get(key)!.items.push({ part, vessel: v });
    }
  }
  return Array.from(buckets.values()).map<Category>((b) => ({
    id: 'engines',
    label: b.label,
    items: b.items,
    summary: { count: b.items.length },
  }));
}

/** RCS — a single flat category (no recipe grouping). */
export function categorizeRcs(
  assembly: AssemblyModel,
  scope: Scope,
): Category[] {
  const vessels = scopedVessels(assembly, scope);
  const items: TaggedPart[] = [];
  for (const v of vessels) {
    for (const part of v.parts) {
      if (part.kind === 'rcs') items.push({ part, vessel: v });
    }
  }
  if (items.length === 0) return [];
  return [
    {
      id: 'rcs',
      label: 'RCS BLOCKS',
      items,
      summary: { count: items.length },
    },
  ];
}

/** Attitude — torque sources: reaction wheels and pod-built-in SAS. */
export function categorizeAttitude(
  assembly: AssemblyModel,
  scope: Scope,
): Category[] {
  const vessels = scopedVessels(assembly, scope);
  const items: TaggedPart[] = [];
  for (const v of vessels) {
    for (const part of v.parts) {
      if (part.sasTorque && part.sasTorque > 0) {
        items.push({ part, vessel: v });
      }
    }
  }
  if (items.length === 0) return [];
  return [
    {
      id: 'attitude',
      label: 'SAS / REACTION WHEELS',
      items,
      summary: { count: items.length },
    },
  ];
}

/** Sum of enabled SAS torque across scope. */
export function totalSasTorque(
  assembly: AssemblyModel,
  scope: Scope,
): number {
  const vessels = scopedVessels(assembly, scope);
  let t = 0;
  for (const v of vessels) {
    for (const part of v.parts) {
      if (part.sasTorque) t += part.sasTorque;
    }
  }
  return t;
}

/** Sum of RCS thrust in scope. */
export function totalRcsThrust(
  assembly: AssemblyModel,
  scope: Scope,
): number {
  const vessels = scopedVessels(assembly, scope);
  let t = 0;
  for (const v of vessels) {
    for (const part of v.parts) {
      if (part.kind === 'rcs' && part.engine) t += part.engine.thrust;
    }
  }
  return t;
}

/** Sum of tank mass across all resources in scope. */
export function totalTankageMass(
  assembly: AssemblyModel,
  scope: Scope,
): number {
  const vessels = scopedVessels(assembly, scope);
  let m = 0;
  for (const v of vessels) {
    for (const part of v.parts) {
      if (!part.tanks) continue;
      for (const key of Object.keys(part.tanks) as ResourceId[]) {
        const t = part.tanks[key];
        if (t) m += t.current * PROPELLANT_DENSITY[key];
      }
    }
  }
  return m;
}

/* ---------------- Stats: ΔV / TWR / burn time ---------------- */

export interface PropulsionStats {
  /** True when at least one engine in scope is enabled and has fuel to burn. */
  hasThrust: boolean;
  /** Sum of enabled engine thrust, kN (vacuum). */
  totalThrust: number;
  /** Mass-flow-weighted vacuum ISP across enabled engines, seconds. */
  weightedIsp: number;
  /** Structural dry mass + unburnable propellants (e.g. monoprop for LF/Ox engines). */
  dryMass: number;
  /** Total tank mass across all resources, tonnes. */
  propellantMass: number;
  /** Propellant mass the enabled engines can actually burn, tonnes. */
  burnableFuelMass: number;
  /** Dry + burnable, tonnes. */
  wetMass: number;
  /** Vacuum ΔV, m/s. Zero when no enabled engines or no burnable fuel. */
  deltaV: number;
  /** Thrust-to-weight at Kerbin surface gravity, dimensionless. */
  twrKerbin: number;
  /** Burn time at full throttle until burnable propellant depletion, seconds. */
  burnTimeSeconds: number;
  /** Count of engines in scope and how many are enabled. */
  engineCount: number;
  enabledEngineCount: number;
}

/**
 * Compute propulsion stats for the scoped vessel(s). Only engines whose
 * `enabled[id] ?? true` is true contribute thrust. Propellants are split
 * into "burnable" (consumed by the enabled main engines) and "unburnable"
 * (e.g. monoprop while the only active engine uses LF/Ox) — the latter
 * stays in the dry-mass term of the rocket equation.
 */
export function computePropulsionStats(
  assembly: AssemblyModel,
  scope: Scope,
  enabled: Record<string, boolean>,
): PropulsionStats {
  const vessels: VesselModel[] =
    scope.kind === 'assembly'
      ? assembly.vessels
      : [assembly.vessels.find((v) => v.id === scope.id)!];

  let structuralDryMass = 0;
  let totalThrust = 0;
  let ispNumerator = 0; // Σ (thrust / isp) — for mass-flow-weighted ISP
  let engineCount = 0;
  let enabledEngineCount = 0;
  const burnablePropellants = new Set<ResourceId>();

  // First pass: account for engines, record which propellants get burned.
  for (const v of vessels) {
    structuralDryMass += v.dryMass;
    for (const part of v.parts) {
      if (part.kind === 'engine' && part.engine) {
        engineCount += 1;
        const on = enabled[part.id] ?? true;
        if (on) {
          enabledEngineCount += 1;
          totalThrust += part.engine.thrust;
          ispNumerator += part.engine.thrust / part.engine.ispVac;
          for (const prop of part.engine.propellants) {
            burnablePropellants.add(prop);
          }
        }
      }
    }
  }

  // Second pass: partition tank mass into burnable vs unburnable.
  let burnableFuelMass = 0;
  let unburnablePropMass = 0;
  let propellantMass = 0;
  for (const v of vessels) {
    for (const part of v.parts) {
      if (!part.tanks) continue;
      for (const key of Object.keys(part.tanks) as ResourceId[]) {
        const t = part.tanks[key];
        if (!t) continue;
        const m = t.current * PROPELLANT_DENSITY[key];
        propellantMass += m;
        if (burnablePropellants.has(key)) burnableFuelMass += m;
        else unburnablePropMass += m;
      }
    }
  }

  const dryMass = structuralDryMass + unburnablePropMass;
  const wetMass = dryMass + burnableFuelMass;
  const hasThrust = totalThrust > 0 && burnableFuelMass > 0;

  if (!hasThrust) {
    return {
      hasThrust: false,
      totalThrust,
      weightedIsp: 0,
      dryMass,
      propellantMass,
      burnableFuelMass,
      wetMass,
      deltaV: 0,
      twrKerbin: 0,
      burnTimeSeconds: 0,
      engineCount,
      enabledEngineCount,
    };
  }

  const weightedIsp = totalThrust / ispNumerator;
  const exhaustVelocity = weightedIsp * G0; // m/s
  const deltaV = exhaustVelocity * Math.log(wetMass / dryMass);
  const twrKerbin = (totalThrust * 1000) / (wetMass * 1000 * G0);
  const massFlow = (totalThrust * 1000) / exhaustVelocity; // kg/s
  const burnTimeSeconds = (burnableFuelMass * 1000) / massFlow;

  return {
    hasThrust: true,
    totalThrust,
    weightedIsp,
    dryMass,
    propellantMass,
    burnableFuelMass,
    wetMass,
    deltaV,
    twrKerbin,
    burnTimeSeconds,
    engineCount,
    enabledEngineCount,
  };
}

export function formatBurnTime(seconds: number): string {
  if (seconds <= 0) return '—';
  if (seconds < 60) return `${seconds.toFixed(0)}s`;
  if (seconds < 3600) {
    const m = Math.floor(seconds / 60);
    const s = Math.round(seconds % 60);
    return `${m}m ${s.toString().padStart(2, '0')}s`;
  }
  const h = Math.floor(seconds / 3600);
  const m = Math.round((seconds % 3600) / 60);
  return `${h}h ${m.toString().padStart(2, '0')}m`;
}
