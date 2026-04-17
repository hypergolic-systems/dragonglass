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
  return flightScratch;
}
