import { getKsp } from './context';
import { FlightTopic } from '../core/topics';
import type { FlightOps } from '../core/flight-ops';

/**
 * Typed handle for invoking operations on the active vessel. Must be
 * called during component initialization (needs Svelte context).
 * Returns a stable object whose methods wrap `Ksp.send` so call sites
 * read as `ops.setSas(true)` rather than the full `telemetry.send(...)`
 * form.
 */
export function useFlightOps(): FlightOps {
  const telemetry = getKsp();
  return {
    setSas: (enabled) => telemetry.send(FlightTopic, 'setSas', enabled),
    setRcs: (enabled) => telemetry.send(FlightTopic, 'setRcs', enabled),
  };
}
