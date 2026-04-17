// Client-side attitude smoother for the navball. The flight topic
// arrives at ~10 Hz; we integrate the latched angular velocity forward
// between wire frames so the rendered attitude tracks the craft at
// monitor refresh rate, and reconcile via exponential slerp toward a
// *moving* target rather than snapping when a new frame lands.
//
// Convention: `omega` is angular velocity in the **body frame**
// (rad/s), matching the wire's `FlightData.angularVelocity`. The
// integration step uses right-multiplication — `q_new = q * dq(ω·dt)`
// — which is the correct composition for a body-frame rate applied to
// a body-to-world quaternion. This mirrors the working pattern in
// `simulated/flight-sim.ts`.
//
// Pure TS, no Svelte / threlte / DOM dependencies.

import { Quaternion, Vector3 } from 'three';

export class AttitudePredictor {
  private readonly tauMs: number;

  // Last authoritative observation.
  private readonly qObs = new Quaternion();
  private readonly wObs = new Vector3();
  private tObs = -Infinity;

  // Smoothed output state.
  private readonly qOut = new Quaternion();
  private tOut = -Infinity;

  // Scratch.
  private readonly qTarget = new Quaternion();
  private readonly qDelta = new Quaternion();
  private readonly wAxis = new Vector3();

  constructor(tauMs = 100) {
    this.tauMs = tauMs;
  }

  /** Latch a freshly-arrived telemetry frame. */
  observe(q: Quaternion, omega: Vector3, tMs: number): void {
    this.qObs.copy(q);
    this.wObs.copy(omega);
    this.tObs = tMs;

    // First observation seeds the output so we don't ease in from the
    // default identity pose.
    if (this.tOut < 0) {
      this.qOut.copy(q);
      this.tOut = tMs;
    }
  }

  /**
   * Compute the smoothed orientation at `tMs` and write it into `out`.
   * Call once per render frame.
   *
   * Target is the authoritative pose extrapolated forward by ω from
   * the last observation; output approaches target exponentially with
   * time constant τ. In steady state (constant ω, constant latency)
   * this lands on top of the target — no residual lag, no snap.
   */
  sample(tMs: number, out: Quaternion): void {
    if (this.tObs < 0) {
      out.copy(this.qOut);
      return;
    }

    const dtPredSec = Math.max(0, (tMs - this.tObs) * 0.001);
    this.qTarget.copy(this.qObs);
    this.integrateBody(this.qTarget, this.wObs, dtPredSec);

    const dtFrameSec = Math.max(0, (tMs - this.tOut) * 0.001);
    const alpha = 1 - Math.exp((-dtFrameSec * 1000) / this.tauMs);
    this.qOut.slerp(this.qTarget, alpha);
    this.tOut = tMs;

    out.copy(this.qOut);
  }

  private integrateBody(q: Quaternion, omega: Vector3, dt: number): void {
    const w = omega.length();
    if (w * dt < 1e-9) return;
    this.wAxis.copy(omega).divideScalar(w);
    this.qDelta.setFromAxisAngle(this.wAxis, w * dt);
    q.multiply(this.qDelta);
  }
}
