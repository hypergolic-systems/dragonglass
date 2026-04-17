// Pure derivations over the assembly model for the Power subsystem view.
//
// The "boundary flow" attribution is a synthesis, not a game-internal
// measurement: docked vessels share a single EC pool, so there is no real
// wire to meter. We attribute the minimum of (one side's deficit, the
// other side's surplus) to the docking port so the per-vessel view can
// honestly show where its power is coming from without pretending the
// pool doesn't exist.

import type { AssemblyModel, PartModel, VesselModel } from '@dragonglass/telemetry/core';

export type Scope = { kind: 'assembly' } | { kind: 'vessel'; id: string };

export interface VesselPowerSummary {
  vessel: VesselModel;
  gen: number;
  draw: number;
  net: number;
  storage: { current: number; capacity: number };
  generators: PartModel[];
  consumers: PartModel[];
  batteries: PartModel[];
  idle: PartModel[];
}

export interface AssemblyPowerSummary {
  vessels: VesselPowerSummary[];
  gen: number;
  draw: number;
  net: number;
  storage: { current: number; capacity: number };
}

export interface BoundaryFlow {
  portName: string;
  partnerVesselId: string;
  partnerVesselName: string;
  partnerPortName: string;
  /** +inflow / −outflow, from *this* vessel's perspective. */
  ecFlow: number;
}

export function summarizeVessel(v: VesselModel): VesselPowerSummary {
  let gen = 0;
  let draw = 0;
  let stCur = 0;
  let stCap = 0;
  const generators: PartModel[] = [];
  const consumers: PartModel[] = [];
  const batteries: PartModel[] = [];
  const idle: PartModel[] = [];
  for (const p of v.parts) {
    if (p.ecFlow && p.ecFlow > 0) {
      gen += p.ecFlow;
      generators.push(p);
    } else if (p.ecFlow && p.ecFlow < 0) {
      draw += -p.ecFlow;
      consumers.push(p);
    }
    if (p.ecStorage) {
      stCur += p.ecStorage.current;
      stCap += p.ecStorage.capacity;
      batteries.push(p);
    }
    if (!p.ecFlow && !p.ecStorage) {
      idle.push(p);
    }
  }
  return {
    vessel: v,
    gen,
    draw,
    net: gen - draw,
    storage: { current: stCur, capacity: stCap },
    generators,
    consumers,
    batteries,
    idle,
  };
}

export function summarizeAssembly(a: AssemblyModel): AssemblyPowerSummary {
  const vessels = a.vessels.map(summarizeVessel);
  let gen = 0;
  let draw = 0;
  let stCur = 0;
  let stCap = 0;
  for (const s of vessels) {
    gen += s.gen;
    draw += s.draw;
    stCur += s.storage.current;
    stCap += s.storage.capacity;
  }
  return {
    vessels,
    gen,
    draw,
    net: gen - draw,
    storage: { current: stCur, capacity: stCap },
  };
}

/**
 * Attribute a single boundary flow across a vessel's connected port.
 * Works cleanly in the two-vessel case; more sophisticated attribution
 * would be required for three-or-more-vessel complexes.
 */
export function boundaryFlowFor(
  a: AssemblyModel,
  v: VesselModel,
): BoundaryFlow | null {
  const port = v.ports.find((p) => p.connectedTo);
  if (!port || !port.connectedTo) return null;
  const partner = a.vessels.find((x) => x.id === port.connectedTo!.vesselId);
  if (!partner) return null;
  const partnerPort = partner.ports.find(
    (p) => p.id === port.connectedTo!.portId,
  );

  const self = summarizeVessel(v);
  const other = summarizeVessel(partner);
  const selfDeficit = self.draw - self.gen;
  const partnerDeficit = other.draw - other.gen;

  let ecFlow = 0;
  if (selfDeficit > 0 && partnerDeficit < 0) {
    ecFlow = Math.min(selfDeficit, -partnerDeficit);
  } else if (selfDeficit < 0 && partnerDeficit > 0) {
    ecFlow = -Math.min(-selfDeficit, partnerDeficit);
  }

  return {
    portName: port.name,
    partnerVesselId: partner.id,
    partnerVesselName: partner.name,
    partnerPortName: partnerPort?.name ?? '',
    ecFlow,
  };
}

/* ---------------- Functional categorization for the Power accordion ---------------- */

export type CategoryId =
  // Power subsystem
  | 'generation'
  | 'batteries'
  | 'command'
  | 'lifesupport'
  | 'science'
  | 'comms'
  | 'vessels'
  // Propulsion subsystem
  | 'engines'
  | 'rcs'
  | 'propellant'
  | 'attitude';

export interface TaggedPart {
  part: PartModel;
  vessel: VesselModel;
}

export interface CategorySummary {
  gen?: number;
  draw?: number;
  storage?: { current: number; capacity: number };
  count: number;
}

export interface VesselEntry {
  vessel: VesselModel;
  kind: 'member' | 'partner';
  /** Member: own net EC. Partner: boundary flow from self's perspective. */
  flow: number;
  summary: VesselPowerSummary;
  /** Partner only. */
  portName?: string;
  /** Partner only. */
  partnerPortName?: string;
}

export interface Category {
  id: CategoryId;
  label: string;
  items: TaggedPart[];
  summary: CategorySummary;
  /** Only set for id === 'vessels'. */
  vessels?: VesselEntry[];
}

const FUNCTIONAL_CATEGORY: Record<PartModel['kind'], CategoryId | null> = {
  battery: 'batteries',
  solar: 'generation',
  rtg: 'generation',
  science: 'science',
  pod: 'command',
  antenna: 'comms',
  engine: null,
  rcs: null,
  tank: null,
  port: null,
};

const CATEGORY_ORDER: CategoryId[] = [
  'generation',
  'batteries',
  'command',
  'lifesupport',
  'science',
  'comms',
  'vessels',
];

const CATEGORY_LABELS: Record<CategoryId, string> = {
  // Power
  generation: 'GENERATION',
  batteries: 'STORAGE',
  command: 'COMMAND',
  lifesupport: 'LIFE SUPPORT',
  science: 'SCIENCE',
  comms: 'COMMUNICATIONS',
  vessels: 'VESSELS',
  // Propulsion
  engines: 'ENGINES',
  rcs: 'RCS',
  propellant: 'PROPELLANT',
  attitude: 'ATTITUDE',
};

function summarizeCategoryItems(items: TaggedPart[]): CategorySummary {
  let gen = 0;
  let draw = 0;
  let stCur = 0;
  let stCap = 0;
  for (const { part } of items) {
    if (part.ecFlow && part.ecFlow > 0) gen += part.ecFlow;
    if (part.ecFlow && part.ecFlow < 0) draw += -part.ecFlow;
    if (part.ecStorage) {
      stCur += part.ecStorage.current;
      stCap += part.ecStorage.capacity;
    }
  }
  return {
    gen: gen > 0 ? gen : undefined,
    draw: draw > 0 ? draw : undefined,
    storage: stCap > 0 ? { current: stCur, capacity: stCap } : undefined,
    count: items.length,
  };
}

export function categorizePower(
  assembly: AssemblyModel,
  scope: Scope,
): Category[] {
  const scopedVessels: VesselModel[] =
    scope.kind === 'assembly'
      ? assembly.vessels
      : [assembly.vessels.find((v) => v.id === scope.id)!];

  const buckets: Partial<Record<CategoryId, TaggedPart[]>> = {};

  for (const vessel of scopedVessels) {
    for (const part of vessel.parts) {
      const cat = FUNCTIONAL_CATEGORY[part.kind];
      if (!cat) continue;
      (buckets[cat] ??= []).push({ part, vessel });
    }
  }

  // Always emit every category in canonical order, even when empty, so the
  // subsystem structure stays stable across scopes.
  const categories: Category[] = [];
  for (const id of CATEGORY_ORDER) {
    if (id === 'vessels') continue;
    const items = buckets[id] ?? [];
    categories.push({
      id,
      label: CATEGORY_LABELS[id],
      items,
      summary: summarizeCategoryItems(items),
    });
  }

  // VESSELS category: assembly scope shows member vessels; per-vessel scope
  // shows docked partners via boundary flow attribution.
  const vesselEntries: VesselEntry[] = [];
  if (scope.kind === 'assembly') {
    for (const v of assembly.vessels) {
      const vs = summarizeVessel(v);
      vesselEntries.push({ vessel: v, kind: 'member', flow: vs.net, summary: vs });
    }
  } else {
    const self = scopedVessels[0];
    const flow = boundaryFlowFor(assembly, self);
    if (flow) {
      const partnerVessel = assembly.vessels.find(
        (v) => v.id === flow.partnerVesselId,
      );
      if (partnerVessel) {
        vesselEntries.push({
          vessel: partnerVessel,
          kind: 'partner',
          flow: flow.ecFlow,
          summary: summarizeVessel(partnerVessel),
          portName: flow.portName,
          partnerPortName: flow.partnerPortName,
        });
      }
    }
  }
  categories.push({
    id: 'vessels',
    label: CATEGORY_LABELS.vessels,
    items: [],
    summary: { count: vesselEntries.length },
    vessels: vesselEntries,
  });

  return categories;
}

/** Format "time until empty/full" given a signed EC/s and storage state. */
export function estimateTime(
  netFlow: number,
  storage: { current: number; capacity: number },
): string {
  if (netFlow === 0 || storage.capacity === 0) return 'steady';
  const secs =
    netFlow > 0
      ? ((storage.capacity - storage.current) / netFlow) * 1
      : (storage.current / -netFlow) * 1;
  if (!isFinite(secs) || secs < 0) return 'steady';
  if (secs < 90) return `${Math.round(secs)}s`;
  if (secs < 5400) return `${Math.round(secs / 60)}m`;
  const h = Math.floor(secs / 3600);
  const m = Math.round((secs % 3600) / 60);
  return m > 0 ? `${h}h ${m.toString().padStart(2, '0')}m` : `${h}h`;
}
