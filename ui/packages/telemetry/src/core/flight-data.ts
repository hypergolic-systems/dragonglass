import type { Quaternion, Vector3 } from 'three';

export type SpeedDisplayMode = 'orbit' | 'surface' | 'target';

/**
 * Live flight telemetry for the active vessel. Shape mirrors the KSP
 * server's `flight` topic wire format.
 *
 * Directional fields (velocities + orientation + angular velocity)
 * are in a **surface frame** anchored at the vessel:
 *   +Y = planet-radial up, +Z = north, +X = east
 *
 * `surfaceVelocity` is velocity relative to the rotating planet
 * surface; `orbitalVelocity` is in the planet's inertial frame. Both
 * are full 3D vectors — magnitude gives speed, direction places
 * prograde/retrograde markers on the navball.
 */
/**
 * Frames are `readonly` on every field. The telemetry pipeline
 * must publish a fresh frame per tick (new scalars, new Vector3 /
 * Quaternion references) rather than mutating a scratch object in
 * place — in-place mutation on nested class instances (Vector3,
 * Quaternion) bypasses Svelte's `$state` proxy and breaks downstream
 * reactivity. The type system is the enforcement mechanism; there
 * is no runtime defensive cloning.
 */
export interface FlightData {
  readonly vesselId: string;
  readonly altitudeAsl: number;        // meters above sea level
  readonly altitudeRadar: number;      // meters above terrain
  readonly surfaceVelocity: Vector3;   // m/s, surface frame
  readonly orbitalVelocity: Vector3;   // m/s, surface frame
  readonly throttle: number;           // [0, 1]
  readonly sas: boolean;
  readonly rcs: boolean;
  readonly orientation: Quaternion;
  readonly angularVelocity: Vector3;   // rad/s, body frame
  readonly hasTarget: boolean;
  /**
   * Target-relative orbital velocity in the surface frame, m/s.
   * Mirrors stock KSP's
   * `ship_tgtVelocity = ship_obtVelocity − target.GetObtVelocity()`.
   * Only meaningful when `hasTarget` is true; drives the navball's
   * target-prograde / target-retrograde markers.
   */
  readonly targetVelocity: Vector3;
  /** Summed instantaneous engine thrust on the active vessel, kN. */
  readonly currentThrust: number;
  /** Stock KSP speed-display mode. Drives the speed tape's readout
   *  and label, and the navball's prograde/retrograde marker source:
   *    'orbit'   → orbitalVelocity
   *    'surface' → surfaceVelocity
   *    'target'  → targetVelocity (meaningful only when hasTarget). */
  readonly speedDisplayMode: SpeedDisplayMode;
}
