// Shared `StageData` store — see `use-flight.svelte.ts` for the
// pattern rationale. One `$state` value, one subscription, many
// consumers.

import { getKsp } from './context';
import { StageTopic } from '../core/topics';
import type { StageData, StageEntry } from '../core/stage-data';

interface MutableStageData {
  vesselId: string;
  currentStageIdx: number;
  stages: readonly StageEntry[];
}

function defaults(): MutableStageData {
  return { vesselId: '', currentStageIdx: -1, stages: [] };
}

const store = $state<MutableStageData>(defaults());
let subscribed = false;

/**
 * Subscribe to the stage telemetry topic. Returns the shared reactive
 * `StageData` store — fields and the nested `stages` array track
 * Svelte dependencies. Server only emits when a material change is
 * detected (stage count, reorder, dV/TWR/burn-time epsilon, or a
 * part moving between stages), so stable frames are cheap on the
 * wire.
 *
 * Must be called during component initialization (needs Svelte context).
 */
export function useStageData(): StageData {
  if (!subscribed) {
    subscribed = true;
    const telemetry = getKsp();
    telemetry.subscribe(StageTopic, (frame) => {
      store.vesselId = frame.vesselId;
      store.currentStageIdx = frame.currentStageIdx;
      store.stages = frame.stages;
    });
  }
  return store as StageData;
}
