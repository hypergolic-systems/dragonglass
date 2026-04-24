// Generic client-side smoother.
//
// Bridges a slow, jittery source (~10 Hz wire) and a fast renderer
// (vsync). On every observation, latches the new value and (a wire-
// supplied or finite-differenced) velocity. On every sample, projects
// the latched value forward by the velocity to a *moving* target,
// then exponentially eases the output toward that target. In steady
// state — constant velocity, constant latency — the output lands on
// top of the target with no residual lag and no snap. New
// observations don't jolt the output because the same exponential
// ease handles them too.
//
// Two ways to drive output:
//  - **Pull**: caller invokes `sample(t, out)` from its own loop
//    (typical in a 3D scene that already has a per-frame tick).
//  - **Push**: caller `subscribe()`s to RAF-rate updates dispatched
//    by `SmoothedRegistry`. The smoother registers itself with the
//    shared registry on first subscriber and unregisters on last —
//    so an unmounted-but-not-yet-GC'd `Smoothed` costs zero per frame.
//
// The two modes coexist: a smoother with both `sample()` callers and
// `subscribe()` subscribers updates correctly for both.

import type { Kinematic, SmoothingOptions } from './kinematic';
import { SmoothedRegistry } from './registry';

const DEFAULT_TAU_SEC = 0.1;

// Cap on how far forward we'll project from the last observation.
// Defends against pathological gaps (paused tab, network stall,
// snapshot replay of a topic that hasn't moved in minutes) where an
// honest `dt = now - tObserved` would produce wildly extrapolated
// values. 0.5 s is well past the wire's 10 Hz cadence (~100 ms
// inter-frame) so normal jitter doesn't trip it.
const MAX_PROJECT_SEC = 0.5;

export class Smoothed<T, V = T> {
  private readonly kin: Kinematic<T, V>;
  private readonly tauSec: number;
  private readonly velSource: 'wire' | 'fd';
  private readonly clamp: ((v: T) => void) | undefined;

  // Latched observation. `obs` doubles as "previous observation" for
  // the FD-velocity path: at the top of `observe()` it still holds
  // the prior frame, which we diff against the new value before
  // overwriting.
  private readonly obs: T;
  private readonly vel: V;
  private tObs = -Infinity;
  private hasObs = false;

  // Smoothed output state.
  private readonly out: T;
  private tOut = -Infinity;

  // Scratch.
  private readonly target: T;

  // Push-mode subscribers.
  private readonly subs = new Set<(out: T) => void>();
  private registered = false;

  constructor(kin: Kinematic<T, V>, options: SmoothingOptions<T>) {
    this.kin = kin;
    this.tauSec = options.tauSec ?? DEFAULT_TAU_SEC;
    this.velSource = options.velocity;
    this.clamp = options.clamp;

    if (this.velSource === 'fd' && !kin.diff) {
      throw new Error("Smoothed: velocity 'fd' requires Kinematic.diff");
    }

    this.obs = kin.alloc();
    this.vel = kin.allocV();
    this.out = kin.alloc();
    this.target = kin.alloc();
  }

  /**
   * Latch a freshly-arrived observation. `t` is the local-clock time
   * (seconds, same units as `performance.now() / 1000`) at which the
   * value was observed. `velocity` is required when this `Smoothed`
   * was constructed with `velocity: 'wire'` and ignored otherwise.
   */
  observe(value: T, t: number, velocity?: V): void {
    if (this.velSource === 'wire') {
      if (velocity === undefined) {
        throw new Error("Smoothed: velocity required when velocity: 'wire'");
      }
      // Copy because callers reuse their own vectors across frames.
      this.kin.copyV(this.vel, velocity);
    } else {
      // FD: diff the prior observation (still in `this.obs`) against
      // the new `value` before we overwrite. First observation has no
      // prior to diff against — vel stays zero from allocV.
      if (this.hasObs) {
        const dt = Math.max(0, t - this.tObs);
        // kin.diff existence is enforced in the constructor.
        this.kin.diff!(this.obs, value, dt, this.vel);
      }
    }

    this.kin.copy(this.obs, value);
    this.tObs = t;
    this.hasObs = true;

    // First observation seeds the output so we don't ease in from
    // whatever the alloc() default is.
    if (this.tOut < 0) {
      this.kin.copy(this.out, value);
      this.tOut = t;
    }
  }

  /**
   * Compute the smoothed value at `t` and write it into `out`. Safe
   * to call before any observation has arrived — writes the alloc()
   * default (e.g. identity quaternion, zero vector). Idempotent
   * within a single tick: calling it twice with the same `t`
   * advances the internal state by zero.
   */
  sample(t: number, out: T): void {
    if (!this.hasObs) {
      this.kin.copy(out, this.out);
      return;
    }

    // Forward-project the observation by velocity, capped to defend
    // against extreme gaps (see MAX_PROJECT_SEC).
    const dtPred = Math.min(MAX_PROJECT_SEC, Math.max(0, t - this.tObs));
    this.kin.integrate(this.obs, this.vel, dtPred, this.target);

    // Exponential ease toward the moving target.
    const dtFrame = Math.max(0, t - this.tOut);
    const alpha = 1 - Math.exp(-dtFrame / this.tauSec);
    this.kin.lerp(this.out, this.target, alpha, this.out);

    if (this.clamp) this.clamp(this.out);

    this.tOut = t;
    this.kin.copy(out, this.out);
  }

  /**
   * Subscribe to RAF-rate smoothed updates. The callback receives the
   * smoother's internal output buffer (do not retain — it is mutated
   * on the next tick). Returns an unsubscribe function. Registers
   * this smoother with the shared `SmoothedRegistry` on the first
   * subscriber; unregisters on the last.
   */
  subscribe(cb: (out: T) => void): () => void {
    this.subs.add(cb);
    if (!this.registered) {
      SmoothedRegistry.add(this);
      this.registered = true;
    }
    // Fire once with the current state so late-mounting subscribers
    // don't see stale Svelte defaults until the next RAF.
    cb(this.out);
    return () => {
      this.subs.delete(cb);
      if (this.subs.size === 0 && this.registered) {
        SmoothedRegistry.remove(this);
        this.registered = false;
      }
    };
  }

  /**
   * Called by `SmoothedRegistry` once per RAF. Updates internal state
   * to time `t` and fans out to push-mode subscribers. Distinct from
   * `sample()` because this is the *driving* tick — `sample()` is
   * non-destructive in that it advances state to `t` but a subsequent
   * `sample(t)` is a no-op; both pathways share that property.
   */
  tick(t: number): void {
    if (this.subs.size === 0) return;  // shouldn't happen — defensive
    this.sample(t, this.out);
    for (const cb of this.subs) cb(this.out);
  }
}
