// Shared `CurrentStageData` store — see `use-flight.svelte.ts`
// for the pattern rationale. One `$state`, one subscription,
// many consumers.

import { getKsp } from './context';
import { CurrentStageTopic } from '../core/topics';
import type {
  CurrentStageData,
  EngineGroup,
} from '../core/current-stage-data';

interface MutableCurrentStageData {
  stageIdx: number;
  deltaVStage: number;
  twrStage: number;
  groups: readonly EngineGroup[];
}

function defaults(): MutableCurrentStageData {
  return { stageIdx: 0, deltaVStage: 0, twrStage: 0, groups: [] };
}

const store = $state<MutableCurrentStageData>(defaults());
let subscribed = false;

/**
 * Subscribe to the current-stage telemetry topic. Returns the
 * shared reactive `CurrentStageData` store.
 *
 * Must be called during component initialization (needs Svelte context).
 */
export function useCurrentStageData(): CurrentStageData {
  if (!subscribed) {
    subscribed = true;
    const telemetry = getKsp();
    telemetry.subscribe(CurrentStageTopic, (frame) => {
      store.stageIdx = frame.stageIdx;
      store.deltaVStage = frame.deltaVStage;
      store.twrStage = frame.twrStage;
      store.groups = frame.groups;
    });
  }
  return store as CurrentStageData;
}
