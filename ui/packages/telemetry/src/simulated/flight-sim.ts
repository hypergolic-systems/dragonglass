// Pure TS — no Svelte runes. Owns the sim's private mutable state
// (orientation quaternion, angular-velocity accumulator, elapsed
// time) and emits a **fresh** `FlightData` each tick. Emitting a
// new frame every frame is deliberate: `FlightData` is readonly
// and reactivity downstream depends on reference changes for
// nested Vector3 / Quaternion fields — in-place mutation on a
// class instance bypasses Svelte's `$state` proxy. Allocation
// cost at 60 fps is negligible.

import { Quaternion, Vector3 } from 'three';
import type { FlightData } from '../core/flight-data';
import {
  ENGINES_MAX_THRUST,
  STAGE_IDX_SIM,
  STAGE_DELTA_V_SIM,
  STAGE_TWR_SIM,
} from './engines-fixture';

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
 * Owns the simulation state and advances it each tick. Framework-
 * agnostic — call `tick(dt)` from any loop (rAF, setInterval, etc.)
 * and it returns a fresh immutable `FlightData` for the current
 * frame. The sim's own internal state (orientation, angular
 * velocity, elapsed time) stays mutable behind the class boundary.
 */
export class FlightSimulation {
  readonly keys = new Set<string>();
  resetPending = false;

  private t = 0;
  private throttle = 2 / 3;
  private sas = false;
  private rcs = false;
  private readonly angVel = { x: 0, y: 0, z: 0 };
  private readonly orientation = new Quaternion();

  // Transient scratch objects used inside `tick` to avoid
  // allocating them on every frame. Their values are copied into
  // the fresh frame's own Vector3 / Quaternion instances before
  // the frame is emitted, so the sim's internal references are
  // never exposed to subscribers.
  private readonly dq = new Quaternion();
  private readonly axis = new Vector3();

  setSas(enabled: boolean): void {
    this.sas = enabled;
  }

  setRcs(enabled: boolean): void {
    this.rcs = enabled;
  }

  tick(dt: number): FlightData {
    const { keys, angVel, dq, axis } = this;

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
      this.orientation.multiply(dq).normalize();
    }

    this.t += dt;
    const launch = simulateLaunch(this.t);

    // Sim vessel ascends straight up with a bit of eastward drift so
    // orbital + surface velocities read plausibly. Both vectors in the
    // surface frame: +X = east, +Y = up, +Z = north.
    const eastwardDrift = Math.min(launch.speed * 0.15, 50);

    return {
      vesselId: 'sim-vessel',
      altitudeAsl: launch.altitude,
      altitudeRadar: launch.altitude,
      surfaceVelocity: new Vector3(eastwardDrift, launch.verticalSpeed, 0),
      orbitalVelocity: new Vector3(eastwardDrift + 175, launch.verticalSpeed, 0),
      throttle: this.throttle,
      sas: this.sas,
      rcs: this.rcs,
      orientation: this.orientation.clone(),
      angularVelocity: new Vector3(angVel.x, angVel.y, angVel.z),
      hasTarget: false,
      targetVelocity: new Vector3(),
      deltaVMission: 3800,
      currentThrust: this.throttle * ENGINES_MAX_THRUST,
      stageIdx: STAGE_IDX_SIM,
      deltaVStage: STAGE_DELTA_V_SIM,
      twrStage: STAGE_TWR_SIM,
    };
  }
}
