// Per-type "math interface" for the smoother.
//
// `Smoothed<T, V>` is generic over the *value* type `T` and the
// *velocity* type `V`; the type-specific operations (allocate scratch,
// copy, interpolate, integrate forward by velocity) live behind this
// interface. The smoother itself stays unaware of whether it's
// smoothing a Vec2 screen position, a Vector3 world position, or a
// Quaternion body orientation.
//
// **In-place by convention.** Every operation that produces a value
// writes into a caller-supplied `out` rather than returning a fresh
// allocation. The smoother allocates its scratch once at construction
// and reuses it forever; this keeps a hot-path tick free of GC churn
// even with dozens of smoothed values updating at vsync.
//
// **`out` aliasing is allowed.** `lerp(a, b, alpha, out)` and
// `integrate(state, vel, dt, out)` must both behave correctly when
// `out === a` / `out === state`. Implementations that can't operate
// in place atomically should copy `a` (or `state`) into `out` first
// and then modify `out`. Callers rely on this so a `Smoothed` can
// re-use its current output as the input for next tick's update.

export interface Kinematic<T, V = T> {
  /** Allocate a fresh value. Used for the smoother's scratch buffers. */
  alloc(): T;

  /**
   * Allocate a fresh velocity. The smoother always needs at least one
   * `V` scratch — for `'wire'` strategies to copy the caller's
   * velocity into, for `'fd'` strategies to write the diff result
   * into.
   */
  allocV(): V;

  /** `dst <- src`. */
  copy(dst: T, src: T): void;

  /**
   * `dst <- src` for velocities. For most kinematics `V === T` and
   * this duplicates `copy`; for `quaternionBodyKinematic` `V` is a
   * Vector3 (body-frame angular velocity) distinct from the
   * Quaternion `T`, so the operations differ.
   */
  copyV(dst: V, src: V): void;

  /**
   * Interpolate from `a` toward `b` by fraction `alpha ∈ [0, 1]` and
   * write into `out`. For Quaternion this is shortest-arc slerp. Must
   * be safe when `out === a`.
   */
  lerp(a: T, b: T, alpha: number, out: T): void;

  /**
   * Advance `state` by `vel` over `dt` seconds and write into `out`.
   * Must be safe when `out === state`. Implementations are free to
   * assume the velocity is constant over the step (first-order
   * integration); higher-order integrators would need the smoother
   * to track velocity history, which we deliberately don't.
   */
  integrate(state: T, vel: V, dt: number, out: T): void;

  /**
   * Estimate `vel` such that `integrate(a, vel, dt) ≈ b`, write into
   * `out`. Used by the FD-velocity strategy. Must be safe when
   * `dt <= 0` (e.g. the very first observation has no predecessor) —
   * implementations should write a zero velocity in that case.
   *
   * Optional. Required iff `SmoothingOptions.velocity === 'fd'`.
   */
  diff?(a: T, b: T, dt: number, out: V): void;
}

export interface SmoothingOptions<T> {
  /**
   * Exponential time constant in seconds. The smoothed output
   * approaches the moving target with `1 - exp(-dt / tauSec)` per
   * tick — small `tauSec` snaps fast and harshly, large `tauSec`
   * eases slowly. Default `0.1` (100 ms), which matches the
   * navball's pre-existing AttitudePredictor.
   */
  tauSec?: number;

  /**
   * Where the velocity used for forward-projection comes from.
   *  - `'wire'`: caller supplies it on every `observe()` call (e.g.
   *    angular velocity transmitted alongside orientation).
   *  - `'fd'`:   smoother derives it via finite-difference between
   *    consecutive observations (requires `Kinematic.diff`).
   */
  velocity: 'wire' | 'fd';

  /**
   * Optional clamp applied in-place to the smoothed output after each
   * tick. Use for values that aren't allowed to overshoot a hard
   * limit — e.g. a fraction in [0, 1], or a screen position pinned
   * to the viewport.
   */
  clamp?: (v: T) => void;
}
