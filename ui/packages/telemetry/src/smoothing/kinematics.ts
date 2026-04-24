// Concrete `Kinematic` implementations for the value types we
// actually smooth today. Each one is module-level because
// `Kinematic` is stateless beyond the operations it exposes — the
// scratch they need lives module-locally and is safe to share across
// instances since the smoother iterates instances sequentially within
// a single RAF tick (no reentrancy possible in single-threaded JS).

import { Quaternion, Vector3 } from 'three';
import type { Kinematic } from './kinematic';

// ---- Vec2 (plain {x, y}) ---------------------------------------
//
// Used for screen-space positions (e.g. PartActionWindow anchors).
// Plain object rather than THREE.Vector2 to keep the value identity
// trivial — many of these flow into Svelte `$state` stores where the
// extra prototype methods are noise.

export interface Vec2 {
  x: number;
  y: number;
}

export const vec2Kinematic: Kinematic<Vec2, Vec2> = {
  alloc: () => ({ x: 0, y: 0 }),
  allocV: () => ({ x: 0, y: 0 }),
  copy(dst, src) {
    dst.x = src.x;
    dst.y = src.y;
  },
  copyV(dst, src) {
    dst.x = src.x;
    dst.y = src.y;
  },
  lerp(a, b, alpha, out) {
    out.x = a.x + (b.x - a.x) * alpha;
    out.y = a.y + (b.y - a.y) * alpha;
  },
  integrate(state, vel, dt, out) {
    out.x = state.x + vel.x * dt;
    out.y = state.y + vel.y * dt;
  },
  diff(a, b, dt, out) {
    if (dt <= 0) {
      out.x = 0;
      out.y = 0;
      return;
    }
    out.x = (b.x - a.x) / dt;
    out.y = (b.y - a.y) / dt;
  },
};

// ---- Vec3 (THREE.Vector3) --------------------------------------
//
// World-space velocities, accelerations, etc. Reuses three's existing
// allocation + math so `Smoothed<Vector3>` instances can hand the
// output straight to a three.js consumer without copying.

export const vec3Kinematic: Kinematic<Vector3, Vector3> = {
  alloc: () => new Vector3(),
  allocV: () => new Vector3(),
  copy(dst, src) {
    dst.copy(src);
  },
  copyV(dst, src) {
    dst.copy(src);
  },
  lerp(a, b, alpha, out) {
    if (out !== a) out.copy(a);
    out.lerp(b, alpha);
  },
  integrate(state, vel, dt, out) {
    if (out !== state) out.copy(state);
    out.addScaledVector(vel, dt);
  },
  diff(a, b, dt, out) {
    if (dt <= 0) {
      out.set(0, 0, 0);
      return;
    }
    out.copy(b).sub(a).divideScalar(dt);
  },
};

// ---- Quaternion + body-frame angular velocity ------------------
//
// Mirrors the math from the original AttitudePredictor: integration
// uses right-multiplication (`q_new = q * dq(ω·dt)`), the correct
// composition for a body-frame rate applied to a body-to-world
// quaternion. Matches the working pattern in
// `simulated/flight-sim.ts`.
//
// No `diff` provided. Recovering body angular velocity from two
// orientations requires `2 * (q^-1 * dq/dt)` and only matters if
// nothing on the wire carries ω — for the navball, `FlightData`
// already transmits angular velocity, so the wire-velocity strategy
// is the only one that ever runs. If a caller really wants FD
// orientation smoothing later, slot in `quaternionDiffBody` here.

const _wAxis = new Vector3();
const _qDelta = new Quaternion();

export const quaternionBodyKinematic: Kinematic<Quaternion, Vector3> = {
  alloc: () => new Quaternion(),
  allocV: () => new Vector3(),
  copy(dst, src) {
    dst.copy(src);
  },
  copyV(dst, src) {
    dst.copy(src);
  },
  lerp(a, b, alpha, out) {
    if (out !== a) out.copy(a);
    out.slerp(b, alpha);
  },
  integrate(state, vel, dt, out) {
    if (out !== state) out.copy(state);
    const w = vel.length();
    if (w * dt < 1e-9) return;
    _wAxis.copy(vel).divideScalar(w);
    _qDelta.setFromAxisAngle(_wAxis, w * dt);
    out.multiply(_qDelta);
  },
};
