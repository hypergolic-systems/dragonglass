import { getKsp } from './context';
import { FlightTopic } from '../core/topics';
import { AttitudePredictor } from '../core/attitude-predictor';

/**
 * Subscribe to the flight topic and feed an `AttitudePredictor`.
 * Returns the predictor; the caller samples it from its own render
 * loop (e.g. threlte's `useTask`).
 *
 * We subscribe directly to the topic here rather than going through
 * `useFlightData` so we can record `performance.now()` at callback
 * time — the reconcile window needs a true arrival timestamp.
 *
 * Must be called during component initialization (needs Svelte context).
 */
export function useSmoothedOrientation(tauMs = 100): AttitudePredictor {
  const predictor = new AttitudePredictor(tauMs);
  const telemetry = getKsp();

  $effect(() => {
    return telemetry.subscribe(FlightTopic, (frame) => {
      predictor.observe(
        frame.orientation,
        frame.angularVelocity,
        performance.now(),
      );
    });
  });

  return predictor;
}
