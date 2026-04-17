import { Quaternion } from 'three';
import { getKsp } from './context';
import { FlightTopic } from '../core/topics';
import type { FlightData } from '../core/flight-data';

const DEFAULTS: FlightData = {
  altitude: 0,
  surfaceVelocity: 0,
  orientation: new Quaternion(),
};

/**
 * Subscribe to the flight telemetry topic. Returns a reactive
 * `$state<FlightData>` proxy — reading individual fields gives
 * fine-grained reactivity (e.g. a component reading only
 * `data.altitude` re-renders only when altitude changes).
 *
 * Must be called during component initialization (needs Svelte context).
 */
export function useFlightData(): FlightData {
  const telemetry = getKsp();
  let data = $state<FlightData>({ ...DEFAULTS, orientation: new Quaternion() });

  $effect(() => {
    return telemetry.subscribe(FlightTopic, (frame) => {
      // Copy scalar fields via Object.assign (triggers fine-grained
      // $state updates only for changed properties).
      Object.assign(data, frame);
      // Quaternion: copy components rather than replacing the reference,
      // since downstream code holds the Quaternion ref.
      data.orientation.copy(frame.orientation);
    });
  });

  return data;
}
