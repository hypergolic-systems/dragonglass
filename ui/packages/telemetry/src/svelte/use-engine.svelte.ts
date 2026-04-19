// Shared `EngineData` store — see `use-flight.svelte.ts` for the
// pattern rationale. One `$state` value, one subscription, many
// consumers.

import { getKsp } from './context';
import { EngineTopic } from '../core/topics';
import type { EngineData, EnginePoint } from '../core/engine-data';

interface MutableEngineData {
  vesselId: string;
  engines: readonly EnginePoint[];
}

function defaults(): MutableEngineData {
  return { vesselId: '', engines: [] };
}

const store = $state<MutableEngineData>(defaults());
let subscribed = false;

/**
 * Subscribe to the engine telemetry topic. Returns the shared
 * reactive `EngineData` store — fields and nested `engines`
 * entries track Svelte dependencies. The server only emits when
 * a material change is detected, so stable frames are cheap on
 * the wire.
 *
 * Must be called during component initialization (needs Svelte context).
 */
export function useEngineData(): EngineData {
  if (!subscribed) {
    subscribed = true;
    const telemetry = getKsp();
    telemetry.subscribe(EngineTopic, (frame) => {
      store.vesselId = frame.vesselId;
      store.engines = frame.engines;
    });
  }
  return store as EngineData;
}
