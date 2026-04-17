import {
  PROPELLANT_DENSITY,
  RESOURCE_DISPLAY_NAME,
  type AssemblyModel,
  type PartModel,
  type ResourceId,
} from '@dragonglass/telemetry/core';
import type { CategoryId, Scope, TaggedPart } from './power';

/* ---- Formatting ---- */

export const fmtFlow = (n: number, digits = 2) =>
  (n >= 0 ? '+' : '−') + Math.abs(n).toFixed(digits);

export const fmtStorage = (s: { current: number; capacity: number }) =>
  `${Math.round(s.current)} / ${Math.round(s.capacity)}`;

export const pctVal = (s: { current: number; capacity: number }) =>
  s.capacity === 0 ? 0 : s.current / s.capacity;

export const pctLabel = (s?: { current: number; capacity: number }) =>
  s ? `${Math.round(pctVal(s) * 100)}%` : '—';

/* ---- Part icons ---- */

export const PART_ICON: Record<PartModel['kind'], string> = {
  solar:   '☀',
  rtg:     '⚛',
  battery: '▮',
  pod:     '⬡',
  antenna: '⦚',
  science: '⚗',
  engine:  '▲',
  rcs:     '✦',
  tank:    '⬔',
  port:    '⊙',
};

/* ---- Subsystems ---- */

export type SubsystemId = 'prop' | 'crew' | 'sci' | 'sys' | 'log';

export const SUBSYSTEMS: Array<{ id: SubsystemId; label: string; short: string; enabled: boolean }> = [
  { id: 'prop', short: 'PROP', label: 'PROPULSION', enabled: true },
  { id: 'crew', short: 'CREW', label: 'CREW', enabled: false },
  { id: 'sci', short: 'SCI', label: 'SCIENCE', enabled: false },
  { id: 'sys', short: 'SYS', label: 'SYSTEMS', enabled: true },
  { id: 'log', short: 'LOG', label: 'LOG', enabled: false },
];

export const SUBSYSTEM_LABEL: Record<SubsystemId, string> = {
  prop: 'PROPULSION',
  crew: 'CREW',
  sci: 'SCIENCE',
  sys: 'SYSTEMS',
  log: 'LOG',
};

/* ---- Scope ---- */

export interface ScopeEntry {
  key: string;
  label: string;
  sub: string;
  scope: Scope;
}

export function buildScopeList(assembly: AssemblyModel): ScopeEntry[] {
  return [
    {
      key: 'assembly',
      label: 'ASSEMBLY',
      sub: `${assembly.name} · ${assembly.vessels.length} VESSELS`,
      scope: { kind: 'assembly' },
    },
    ...assembly.vessels.map<ScopeEntry>((v) => ({
      key: v.id,
      label: v.name,
      sub:
        (v.role === 'home' ? 'HOME' : 'VISITOR') +
        (v.callsign ? ` · ${v.callsign}` : ''),
      scope: { kind: 'vessel', id: v.id },
    })),
  ];
}

export function scopeIndex(scopeList: ScopeEntry[], scope: Scope): number {
  return scope.kind === 'assembly'
    ? 0
    : Math.max(
        0,
        scopeList.findIndex(
          (e) => e.scope.kind === 'vessel' && e.scope.id === scope.id,
        ),
      );
}

/* ---- Part on/off state ---- */

export function isToggleable(kind: PartModel['kind'], categoryId: CategoryId): boolean {
  switch (categoryId) {
    case 'generation':
      return kind === 'solar';
    case 'science':
      return kind === 'science';
    case 'comms':
      return kind === 'antenna';
    case 'command':
      return kind === 'pod';
    case 'engines':
      return kind === 'engine';
    case 'rcs':
      return kind === 'rcs';
    case 'attitude':
      return false;
    default:
      return false;
  }
}

export function applyPowerState(
  assembly: AssemblyModel,
  enabled: Record<string, boolean>,
): AssemblyModel {
  const isPowerKind = (k: PartModel['kind']) =>
    k === 'solar' || k === 'science' || k === 'antenna' || k === 'pod';
  return {
    ...assembly,
    vessels: assembly.vessels.map((v) => ({
      ...v,
      parts: v.parts.map((p) => {
        if (!isPowerKind(p.kind)) return p;
        const on = enabled[p.id] ?? true;
        return on ? p : { ...p, ecFlow: 0 };
      }),
    })),
  };
}

export type BulkToggleState = 'on' | 'off' | 'mixed' | null;

export function categoryBulkState(
  items: TaggedPart[],
  enabled: Record<string, boolean>,
  categoryId: CategoryId,
): BulkToggleState {
  const toggleables = items.filter((i) => isToggleable(i.part.kind, categoryId));
  if (toggleables.length === 0) return null;
  let on = 0;
  let off = 0;
  for (const i of toggleables) {
    if (enabled[i.part.id] ?? true) on++;
    else off++;
  }
  if (on > 0 && off === 0) return 'on';
  if (off > 0 && on === 0) return 'off';
  return 'mixed';
}

/* ---- Resource mapping ---- */

export const RESOURCE_BY_LABEL: Record<string, ResourceId> = {
  RP1: 'LF',
  LOX: 'Ox',
  HYDRAZINE: 'Mono',
};

/* ---- Detail rows ---- */

export interface DetailRow {
  k: string;
  v: string;
  tone?: 'good' | 'warn' | 'dim' | 'info' | 'mute';
}

export function detailRowsFor(
  part: PartModel,
  categoryId: CategoryId,
  off: boolean,
  focusResource?: ResourceId,
): DetailRow[] {
  switch (categoryId) {
    case 'batteries': {
      if (!part.ecStorage) return [];
      const soc = Math.round(pctVal(part.ecStorage) * 100);
      return [
        { k: 'CAPACITY', v: `${part.ecStorage.capacity.toFixed(0)} EC` },
        { k: 'CURRENT', v: `${part.ecStorage.current.toFixed(0)} EC`, tone: 'info' },
        { k: 'STATE OF CHARGE', v: `${soc}%`, tone: soc > 25 ? 'good' : 'warn' },
        { k: 'CYCLE COUNT', v: '147' },
        { k: 'STATE', v: 'NOMINAL', tone: 'good' },
      ];
    }
    case 'generation': {
      if (part.kind === 'solar') {
        return [
          {
            k: 'OUTPUT',
            v: off ? 'OFF' : `${fmtFlow(part.ecFlow ?? 0)} EC/s`,
            tone: off ? 'mute' : 'good',
          },
          { k: 'PEAK RATED', v: '+24.40 EC/s @ sun 1.00' },
          { k: 'SUN FACTOR', v: '0.35' },
        ];
      }
      if (part.kind === 'rtg') {
        return [
          { k: 'OUTPUT', v: `${fmtFlow(part.ecFlow ?? 0)} EC/s`, tone: 'good' },
          { k: 'HALF-LIFE', v: '28 yrs' },
          { k: 'STATE', v: 'ALWAYS ON', tone: 'good' },
        ];
      }
      return [];
    }
    case 'command': {
      return [
        {
          k: 'AVIONICS DRAW',
          v: off ? 'OFF' : `${fmtFlow(part.ecFlow ?? 0)} EC/s`,
          tone: off ? 'mute' : 'dim',
        },
        { k: 'SAS TORQUE DRAW', v: '−0.15 EC/s @ full authority' },
      ];
    }
    case 'science': {
      return [
        {
          k: 'EC DRAW',
          v: off ? 'OFF' : `${fmtFlow(part.ecFlow ?? 0)} EC/s`,
          tone: off ? 'mute' : 'dim',
        },
        { k: 'DRAW WHEN ACTIVE', v: '−0.80 EC/s' },
      ];
    }
    case 'comms': {
      return [
        {
          k: 'IDLE DRAW',
          v: off ? 'OFF' : `${fmtFlow(part.ecFlow ?? 0)} EC/s`,
          tone: off ? 'mute' : 'dim',
        },
        { k: 'PEAK DRAW', v: '−6.00 EC/s (transmit)' },
      ];
    }
    case 'engines': {
      if (!part.engine) return [];
      const e = part.engine;
      const massFlow = (e.thrust * 1000) / (e.ispVac * 9.81);
      return [
        { k: 'THRUST', v: `${e.thrust.toFixed(1)} kN (vac)`, tone: off ? 'mute' : 'good' },
        { k: 'ISP', v: `${e.ispVac.toFixed(0)} s vac · ${e.ispAtm.toFixed(0)} s atm` },
        { k: 'MASS FLOW', v: `${massFlow.toFixed(2)} kg/s @ full` },
        { k: 'PROPELLANTS', v: e.propellants.join(' + ') },
      ];
    }
    case 'rcs': {
      if (!part.engine) return [];
      const e = part.engine;
      return [
        { k: 'THRUST', v: `${e.thrust.toFixed(1)} kN per block`, tone: off ? 'mute' : 'good' },
        { k: 'ISP', v: `${e.ispVac.toFixed(0)} s vac` },
        { k: 'PROPELLANT', v: e.propellants.join(' + ') },
        { k: 'USAGE', v: 'TRANSLATION · ROTATION AUX' },
      ];
    }
    case 'propellant': {
      if (!part.tanks) return [];
      const keys = Object.keys(part.tanks) as ResourceId[];
      const keysToShow = focusResource && keys.includes(focusResource) ? [focusResource] : keys;
      const rows: DetailRow[] = [];
      for (const key of keysToShow) {
        const t = part.tanks[key];
        if (!t) continue;
        const pctN = Math.round(pctVal(t) * 100);
        const name = RESOURCE_DISPLAY_NAME[key];
        rows.push({
          k: name,
          v: `${t.current.toFixed(0)} / ${t.capacity.toFixed(0)} U (${pctN}%)`,
          tone: 'info',
        });
        rows.push({
          k: name + ' MASS',
          v: `${(t.current * PROPELLANT_DENSITY[key]).toFixed(3)} t`,
        });
      }
      return rows;
    }
    case 'attitude': {
      const torque = part.sasTorque ?? 0;
      return [
        { k: 'SAS TORQUE', v: `${torque.toFixed(1)} kN·m`, tone: 'good' },
        { k: 'SOURCE', v: part.kind === 'pod' ? 'POD · BUILT-IN REACTION WHEEL' : 'DEDICATED' },
        { k: 'EC COST', v: '−0.15 EC/s @ full authority', tone: 'dim' },
      ];
    }
    case 'lifesupport':
    case 'vessels':
    default:
      return [];
  }
}
