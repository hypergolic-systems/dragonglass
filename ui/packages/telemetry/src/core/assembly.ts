export type ResourceId = 'EC' | 'LF' | 'Ox' | 'Mono';

/** Mass per unit, in tonnes. Non-EC resources contribute to vessel mass. */
export const PROPELLANT_DENSITY: Record<ResourceId, number> = {
  EC: 0,
  LF: 0.005,
  Ox: 0.005,
  Mono: 0.004,
};

/** Human-friendly propellant names for the UI. */
export const RESOURCE_DISPLAY_NAME: Record<ResourceId, string> = {
  EC: 'ELECTRIC CHARGE',
  LF: 'RP1',
  Ox: 'LOX',
  Mono: 'HYDRAZINE',
};

export interface Storage {
  current: number;
  capacity: number;
}

export interface EngineSpec {
  /** kN, vacuum. */
  thrust: number;
  /** seconds, vacuum. */
  ispVac: number;
  /** seconds, at Kerbin sea level. */
  ispAtm: number;
  /** Propellants consumed in proportion; used to filter which tanks feed it. */
  propellants: ResourceId[];
}

export interface PartModel {
  id: string;
  name: string;
  kind:
    | 'pod'
    | 'battery'
    | 'solar'
    | 'rtg'
    | 'antenna'
    | 'engine'
    | 'rcs'
    | 'tank'
    | 'science'
    | 'port';
  /** EC flow at current tick. + generates, − draws. Omit for inert parts. */
  ecFlow?: number;
  ecStorage?: Storage;
  /** Non-EC propellant tanks on this part. */
  tanks?: Partial<Record<ResourceId, Storage>>;
  /** Engine / RCS propulsive spec. */
  engine?: EngineSpec;
  /** Attitude-control torque, kN·m (reaction wheels + built-in pod SAS). */
  sasTorque?: number;
  status?: string;
  sub?: string;
}

export interface DockingPortModel {
  id: string;
  name: string;
  connectedTo?: { vesselId: string; portId: string };
}

export interface VesselModel {
  id: string;
  name: string;
  callsign?: string;
  role: 'home' | 'visitor';
  /** Structural dry mass in tonnes (excludes propellant). */
  dryMass: number;
  parts: PartModel[];
  ports: DockingPortModel[];
}

export interface AssemblyModel {
  name: string;
  homeId: string;
  vessels: VesselModel[];
}
