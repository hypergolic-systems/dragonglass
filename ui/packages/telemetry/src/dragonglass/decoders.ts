// Positional-array decoders for the Dragonglass telemetry wire format.
//
// The server emits each topic as `{"topic":"<name>","data":[...]}` with
// `data` a fixed-shape positional array — no field names on the wire.
// Decoders read positions and populate typed objects.
//
// Each decoder returns a **module-scoped scratch singleton**. The
// subscription hooks copy fields out (`Object.assign` for scalars,
// `.copy()` for three.js refs), so reusing the same instance across
// dispatches avoids per-frame allocation at 10 Hz × 3 topics. Callers
// must not retain the returned reference between callbacks.

import { Quaternion, Vector3 } from 'three';
import type { ClockData } from '../core/clock-data';
import type { GameData } from '../core/game-data';
import type { FlightData } from '../core/flight-data';
import type { EngineData, EngineStatus } from '../core/engine-data';
import type { CurrentStageData } from '../core/current-stage-data';

type ClockWire = [number, number | null];
type GameWire = [string, string | null, number];
type FlightWire = [
  string,                              // vesselId
  number,                              // altitudeAsl
  number,                              // altitudeRadar
  [number, number, number],            // surfaceVelocity
  [number, number, number],            // orbitalVelocity
  number,                              // throttle
  boolean,                             // sas
  boolean,                             // rcs
  [number, number, number, number],    // orientation quat (x, y, z, w)
  [number, number, number],            // angular velocity
  [number, number, number] | null,     // target-relative velocity, null if no target
  number,                              // deltaVMission (m/s)
  number,                              // currentThrust (kN)
];

const clockScratch: ClockData = { ut: 0, met: null };

const gameScratch: GameData = {
  scene: '',
  activeVesselId: null,
  timewarp: 1,
};

const flightScratch: FlightData = {
  vesselId: '',
  altitudeAsl: 0,
  altitudeRadar: 0,
  surfaceVelocity: new Vector3(),
  orbitalVelocity: new Vector3(),
  throttle: 0,
  sas: false,
  rcs: false,
  orientation: new Quaternion(),
  angularVelocity: new Vector3(),
  hasTarget: false,
  targetVelocity: new Vector3(),
  deltaVMission: 0,
  currentThrust: 0,
};

export function decodeClock(raw: unknown): ClockData {
  const a = raw as ClockWire;
  clockScratch.ut = a[0];
  clockScratch.met = a[1];
  return clockScratch;
}

export function decodeGame(raw: unknown): GameData {
  const a = raw as GameWire;
  gameScratch.scene = a[0];
  gameScratch.activeVesselId = a[1];
  gameScratch.timewarp = a[2];
  return gameScratch;
}

export function decodeFlight(raw: unknown): FlightData {
  const a = raw as FlightWire;
  flightScratch.vesselId = a[0];
  flightScratch.altitudeAsl = a[1];
  flightScratch.altitudeRadar = a[2];
  const vs = a[3];
  flightScratch.surfaceVelocity.set(vs[0], vs[1], vs[2]);
  const vo = a[4];
  flightScratch.orbitalVelocity.set(vo[0], vo[1], vo[2]);
  flightScratch.throttle = a[5];
  flightScratch.sas = a[6];
  flightScratch.rcs = a[7];
  const q = a[8];
  flightScratch.orientation.set(q[0], q[1], q[2], q[3]);
  const w = a[9];
  flightScratch.angularVelocity.set(w[0], w[1], w[2]);
  const vt = a[10];
  if (vt === null) {
    flightScratch.hasTarget = false;
    flightScratch.targetVelocity.set(0, 0, 0);
  } else {
    flightScratch.hasTarget = true;
    flightScratch.targetVelocity.set(vt[0], vt[1], vt[2]);
  }
  flightScratch.deltaVMission = a[11];
  flightScratch.currentThrust = a[12];
  return flightScratch;
}

// Engine topic. Wire: [vesselId, [ [id, mapX, mapY, status, maxThrust], ... ]]
// Status byte 0=burning, 1=flameout, 2=failed, 3=shutdown — mirror of
// EngineTopic.Classify on the KSP side.
type EngineWire = [
  string,                                                        // vesselId
  Array<[string, number, number, 0 | 1 | 2 | 3, number]>,        // engines
];

const ENGINE_STATUS: readonly EngineStatus[] = [
  'burning',
  'flameout',
  'failed',
  'shutdown',
];

// Engines array gets replaced wholesale each frame so consumers'
// `$derived` (and any engine-map layout recompute) re-runs on
// material change. Keep the `EngineData` envelope itself as a
// scratch singleton, consistent with the other decoders.
const engineScratch: EngineData = {
  vesselId: '',
  engines: [],
};

export function decodeEngines(raw: unknown): EngineData {
  const a = raw as EngineWire;
  engineScratch.vesselId = a[0];
  const src = a[1];
  const out = new Array(src.length);
  for (let i = 0; i < src.length; i++) {
    const e = src[i];
    out[i] = {
      id: e[0],
      x: e[1],
      y: e[2],
      status: ENGINE_STATUS[e[3]],
      maxThrust: e[4],
    };
  }
  engineScratch.engines = out;
  return engineScratch;
}

// Current-stage topic. Wire:
//   [stageIdx, deltaVStage, twrStage,
//    [ [ [engineId, ...], [[resName, avail, cap], ...] ], ... ]]
type CurrentStageWire = [
  number,
  number,
  number,
  Array<[string[], Array<[string, number, number]>]>,
];

const currentStageScratch: CurrentStageData = {
  stageIdx: 0,
  deltaVStage: 0,
  twrStage: 0,
  groups: [],
};

export function decodeCurrentStage(raw: unknown): CurrentStageData {
  const a = raw as CurrentStageWire;
  currentStageScratch.stageIdx = a[0];
  currentStageScratch.deltaVStage = a[1];
  currentStageScratch.twrStage = a[2];
  const srcGroups = a[3];
  const outGroups = new Array(srcGroups.length);
  for (let i = 0; i < srcGroups.length; i++) {
    const g = srcGroups[i];
    const propsRaw = g[1];
    const props = new Array(propsRaw.length);
    for (let j = 0; j < propsRaw.length; j++) {
      const p = propsRaw[j];
      props[j] = { resourceName: p[0], available: p[1], capacity: p[2] };
    }
    outGroups[i] = { engineIds: g[0], propellants: props };
  }
  currentStageScratch.groups = outGroups;
  return currentStageScratch;
}
