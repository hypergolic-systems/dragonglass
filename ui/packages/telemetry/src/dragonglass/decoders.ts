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
import type { FlightData, SpeedDisplayMode } from '../core/flight-data';
import type {
  EngineData,
  EnginePoint,
  EnginePropellant,
  EngineStatus,
} from '../core/engine-data';

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
  number,                              // stageIdx
  number,                              // deltaVStage (m/s)
  number,                              // twrStage
  0 | 1 | 2,                           // speedDisplayMode byte (orbit/surface/target)
];

const SPEED_DISPLAY_MODE: readonly SpeedDisplayMode[] = ['orbit', 'surface', 'target'];

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
  stageIdx: -1,
  deltaVStage: 0,
  twrStage: 0,
  speedDisplayMode: 'surface',
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
  flightScratch.stageIdx = a[13];
  flightScratch.deltaVStage = a[14];
  flightScratch.twrStage = a[15];
  flightScratch.speedDisplayMode = SPEED_DISPLAY_MODE[a[16]];
  return flightScratch;
}

// Engine topic. Wire:
//   [vesselId, [
//     [id, mapX, mapY, status, maxThrust, isp,
//      [crossfeedPartId, ...],
//      [[propName, propAbbr, amount, capacity], ...]
//     ], ...
//   ]]
// Status byte 0=burning, 1=flameout, 2=failed, 3=shutdown, 4=idle —
// mirror of EngineTopic.Classify on the KSP side.
type EnginePropellantWire = [string, string, number, number];
type EngineWire = [
  string,                                    // vesselId
  Array<[
    string,                                  // id
    number,                                  // mapX
    number,                                  // mapY
    0 | 1 | 2 | 3 | 4,                        // status byte
    number,                                  // maxThrust
    number,                                  // isp
    string[],                                // crossfeed part ids
    EnginePropellantWire[],                  // propellants
  ]>,
];

const ENGINE_STATUS: readonly EngineStatus[] = [
  'burning',
  'flameout',
  'failed',
  'shutdown',
  'idle',
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
  const out = new Array<EnginePoint>(src.length);
  for (let i = 0; i < src.length; i++) {
    const e = src[i];
    const propsRaw = e[7];
    const props = new Array<EnginePropellant>(propsRaw.length);
    for (let j = 0; j < propsRaw.length; j++) {
      const p = propsRaw[j];
      props[j] = {
        resourceName: p[0],
        abbr: p[1],
        available: p[2],
        capacity: p[3],
      };
    }
    out[i] = {
      id: e[0],
      x: e[1],
      y: e[2],
      status: ENGINE_STATUS[e[3]],
      maxThrust: e[4],
      isp: e[5],
      crossfeedPartIds: e[6],
      propellants: props,
    };
  }
  engineScratch.engines = out;
  return engineScratch;
}
