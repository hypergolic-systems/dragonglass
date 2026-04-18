// Pure TS — no Svelte runes. Mutates a pre-allocated FlightData
// (plus its nested Quaternion / Vector3) each frame to avoid GC
// pressure at 60fps.

import { Quaternion, Vector3 } from 'three';
import type { FlightData } from '../core/flight-data';

const TORQUE = (35 * Math.PI) / 180;

const LAUNCH_A_BOOST = 90;
const LAUNCH_G = 64;
const LAUNCH_H_CUT = 50_000;
const LAUNCH_T1 = Math.sqrt((2 * LAUNCH_H_CUT) / LAUNCH_A_BOOST);
const LAUNCH_V_CUT = LAUNCH_A_BOOST * LAUNCH_T1;
const LAUNCH_T2 =
  (LAUNCH_V_CUT +
    Math.sqrt(LAUNCH_V_CUT * LAUNCH_V_CUT + 2 * LAUNCH_G * LAUNCH_H_CUT)) /
  LAUNCH_G;
const LAUNCH_CYCLE = LAUNCH_T1 + LAUNCH_T2;

function simulateLaunch(t: number): {
  altitude: number;
  speed: number;
  verticalSpeed: number;
} {
  const tCycle = ((t % LAUNCH_CYCLE) + LAUNCH_CYCLE) % LAUNCH_CYCLE;
  if (tCycle < LAUNCH_T1) {
    return {
      altitude: 0.5 * LAUNCH_A_BOOST * tCycle * tCycle,
      speed: LAUNCH_A_BOOST * tCycle,
      verticalSpeed: LAUNCH_A_BOOST * tCycle,
    };
  }
  const tc = tCycle - LAUNCH_T1;
  const h = LAUNCH_H_CUT + LAUNCH_V_CUT * tc - 0.5 * LAUNCH_G * tc * tc;
  const v = LAUNCH_V_CUT - LAUNCH_G * tc;
  return {
    altitude: Math.max(0, h),
    speed: Math.abs(v),
    verticalSpeed: v,
  };
}

/**
 * Owns the simulation state and advances it each tick.
 * Framework-agnostic — call `tick(dt)` from any loop (rAF, setInterval, etc.)
 * and read `data` for the latest frame.
 */
export class FlightSimulation {
  readonly data: FlightData;

  readonly keys = new Set<string>();
  resetPending = false;

  private t = 0;
  private readonly angVel = { x: 0, y: 0, z: 0 };
  private readonly dq = new Quaternion();
  private readonly axis = new Vector3();

  constructor() {
    this.data = {
      vesselId: 'sim-vessel',
      altitudeAsl: 0,
      altitudeRadar: 0,
      surfaceVelocity: new Vector3(),
      orbitalVelocity: new Vector3(),
      throttle: 1,
      sas: false,
      rcs: false,
      orientation: new Quaternion(),
      angularVelocity: new Vector3(),
      hasTarget: false,
      targetVelocity: new Vector3(),
    };
  }

  setSas(enabled: boolean): void {
    this.data.sas = enabled;
  }

  setRcs(enabled: boolean): void {
    this.data.rcs = enabled;
  }

  tick(dt: number): void {
    const { keys, angVel, dq, axis, data } = this;

    if (this.resetPending) {
      angVel.x = 0;
      angVel.y = 0;
      angVel.z = 0;
      this.resetPending = false;
    }

    if (keys.has('w')) angVel.x += TORQUE * dt;
    if (keys.has('s')) angVel.x -= TORQUE * dt;
    if (keys.has('a')) angVel.y -= TORQUE * dt;
    if (keys.has('d')) angVel.y += TORQUE * dt;
    if (keys.has('q')) angVel.z += TORQUE * dt;
    if (keys.has('e')) angVel.z -= TORQUE * dt;

    const omega = Math.hypot(angVel.x, angVel.y, angVel.z);
    if (omega > 1e-9) {
      axis.set(angVel.x / omega, angVel.y / omega, angVel.z / omega);
      dq.setFromAxisAngle(axis, omega * dt);
      data.orientation.multiply(dq).normalize();
    }

    data.angularVelocity.set(angVel.x, angVel.y, angVel.z);

    this.t += dt;
    const launch = simulateLaunch(this.t);
    data.altitudeAsl = launch.altitude;
    data.altitudeRadar = launch.altitude;

    // Sim vessel ascends straight up with a bit of eastward drift so
    // orbital + surface velocities read plausibly. Both vectors in the
    // surface frame: +X = east, +Y = up, +Z = north.
    const eastwardDrift = Math.min(launch.speed * 0.15, 50);
    data.surfaceVelocity.set(eastwardDrift, launch.verticalSpeed, 0);
    data.orbitalVelocity.set(eastwardDrift + 175, launch.verticalSpeed, 0);
  }
}
