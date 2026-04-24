import { Quaternion, Vector3 } from 'three';
import { getKsp } from './context';
import { FlightTopic } from '../core/topics';
import { Smoothed, quaternionBodyKinematic } from '../smoothing';

/**
 * Subscribe to the flight topic and feed a `Smoothed<Quaternion>`
 * driven by the wire's angular velocity. Returns the smoother; the
 * caller samples it from its own render loop (e.g. threlte's
 * `useTask`) by calling `sample(t, out)` with `t` in seconds.
 *
 * We subscribe directly to the topic here (rather than going through
 * `useFlightData`) so the smoother sees a true per-frame
 * `tObserved` from the transport — important for the reconcile
 * window. Pull-mode driving (vs registering with `SmoothedRegistry`)
 * keeps the navball perfectly aligned with whichever render loop
 * the host scene is using; no separate RAF, no inter-loop drift.
 *
 * Must be called during component initialization (needs Svelte context).
 */
export function useSmoothedOrientation(
  tauSec = 0.1,
): Smoothed<Quaternion, Vector3> {
  const smoothed = new Smoothed<Quaternion, Vector3>(quaternionBodyKinematic, {
    velocity: 'wire',
    tauSec,
  });
  const telemetry = getKsp();

  $effect(() => {
    return telemetry.subscribe(FlightTopic, (frame, tObserved) => {
      smoothed.observe(frame.orientation, tObserved, frame.angularVelocity);
    });
  });

  return smoothed;
}
