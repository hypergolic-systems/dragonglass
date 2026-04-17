import { Quaternion, Vector3 } from 'three';
import { getKsp } from './context';
import { FlightTopic } from '../core/topics';
import type { FlightData } from '../core/flight-data';

function defaults(): FlightData {
  return {
    vesselId: '',
    altitudeAsl: 0,
    altitudeRadar: 0,
    surfaceVelocity: new Vector3(),
    orbitalVelocity: new Vector3(),
    throttle: 0,
    sas: false,
    rcs: false,
    orientation: new Quaternion(),
    angularVelocity: new Vector3(),
    hasTarget: false,
    targetVelocity: new Vector3(),
  };
}

/**
 * Subscribe to the flight telemetry topic. Returns a reactive
 * `$state<FlightData>` proxy — reading individual fields gives
 * fine-grained reactivity (e.g. a component reading only
 * `data.altitudeAsl` re-renders only when altitude changes).
 *
 * Must be called during component initialization (needs Svelte context).
 */
export function useFlightData(): FlightData {
  const telemetry = getKsp();
  let data = $state<FlightData>(defaults());

  $effect(() => {
    return telemetry.subscribe(FlightTopic, (frame) => {
      // Copy scalar fields via Object.assign (triggers fine-grained
      // $state updates only for changed properties).
      Object.assign(data, frame);
      // Quaternion / Vector3: copy components rather than replacing
      // the reference, since downstream code holds these refs.
      data.orientation.copy(frame.orientation);
      data.angularVelocity.copy(frame.angularVelocity);
      data.surfaceVelocity.copy(frame.surfaceVelocity);
      data.orbitalVelocity.copy(frame.orbitalVelocity);
      data.targetVelocity.copy(frame.targetVelocity);
    });
  });

  return data;
}
