import type { Quaternion } from 'three';

export interface FlightData {
  altitude: number;
  surfaceVelocity: number;
  orientation: Quaternion;
}
