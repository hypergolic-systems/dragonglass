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
export interface FlightData {
  vesselId: string;
  altitudeAsl: number;        // meters above sea level
  altitudeRadar: number;      // meters above terrain
  surfaceVelocity: Vector3;   // m/s, surface frame
  orbitalVelocity: Vector3;   // m/s, surface frame
  throttle: number;           // [0, 1]
  sas: boolean;
  rcs: boolean;
  orientation: Quaternion;
  angularVelocity: Vector3;   // rad/s, body frame
}
