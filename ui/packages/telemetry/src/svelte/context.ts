import { DragonglassTelemetry } from '../dragonglass';
import { SimulatedKsp } from '../simulated';
import type { Ksp } from '../core/ksp';

// Singleton Ksp telemetry client. Module-scope rather than svelte
// context so non-component callers (UI mod boot scripts, tests, the
// occasional `import.meta`-driven helper) can read it too. The
// transport is genuinely a singleton — one WebSocket per page —
// so this matches the data flow.
//
// First use auto-bootstraps based on the boot URL:
//   - `?ws=ws://...` → real `DragonglassTelemetry` (sidecar-injected
//     when running inside KSP)
//   - no param         → `SimulatedKsp` for plain-browser dev
//
// `connect()` is itself idempotent, so the auto-bootstrap can call
// it eagerly without preventing later callers from awaiting the
// same promise. UI mods normally just call `useGame()` (or any
// other hook) and the singleton appears.
let instance: Ksp | null = null;

/**
 * Replace the singleton Ksp instance. Optional: `getKsp` auto-
 * bootstraps from the boot URL's `?ws=` param if no instance is
 * set, so production UI mods don't need to call this. Tests and
 * specialty entries (e.g. workbench) use it to inject fixtures or
 * a different transport.
 */
export function setKsp(t: Ksp): void {
  instance = t;
}

/**
 * Return the singleton Ksp telemetry client. Lazily constructs the
 * default — `DragonglassTelemetry` if `?ws=` is present, otherwise
 * `SimulatedKsp` — and calls `connect()` once. Subsequent calls
 * return the cached instance.
 */
export function getKsp(): Ksp {
  if (instance) return instance;
  const wsUrl =
    typeof window !== 'undefined'
      ? new URLSearchParams(window.location.search).get('ws')
      : null;
  instance = wsUrl ? new DragonglassTelemetry(wsUrl) : new SimulatedKsp();
  instance.connect();
  return instance;
}
