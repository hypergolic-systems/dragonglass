import type { Quaternion, Vector3 } from 'three';

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
  /** Total mission remaining Δv, m/s, atmosphere-corrected across all
   *  remaining stages. 0 when KSP's stage simulator hasn't produced a
   *  result yet. */
  readonly deltaVMission: number;
  /** Summed instantaneous engine thrust on the active vessel, kN. */
  readonly currentThrust: number;
  /** KSP's current stage index. Lower numbers = later stages. -1 when
   *  no stage is loaded. */
  readonly stageIdx: number;
  /** Stage remaining Δv, m/s, atmosphere-corrected. 0 when
   *  unavailable. */
  readonly deltaVStage: number;
  /** Stage thrust-to-weight ratio at current conditions. 0 when
   *  unavailable. */
  readonly twrStage: number;
}
