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

/**
 * Optimistic local counterpart to the server's `moveStage` op. Run
 * immediately after `ops.moveStage(...)` so the UI reflects the
 * reorder on the same frame the drop fires — rather than flashing
 * the pre-move state until the server echoes a fresh frame (~10–
 * 100 ms round trip).
 *
 * The reorder math matches `StageTopic.DoMoveStage` on the C# side
 * exactly: for each stage entry, the new stageNum is its old one
 * shifted by ±1 when it sits in the affected range, or the
 * dragged stage's explicit target. No-op when `insertPos` is
 * `fromStageNum` or `fromStageNum + 1` (putting it back where it
 * came from).
 *
 * The server echo that arrives a tick later simply rewrites the
 * store to the same values we just set, so no correction is
 * visible.
 */
export function applyMoveStage(fromStageNum: number, insertPos: number): void {
  const F = fromStageNum;
  const I = insertPos;
  if (I === F || I === F + 1) return;
  const newF = I > F ? I - 1 : I;
  store.stages = store.stages.map((s) => {
    let next = s.stageNum;
    if (s.stageNum === F) next = newF;
    else if (I > F && s.stageNum > F && s.stageNum < I) next = s.stageNum - 1;
    else if (I < F && s.stageNum >= I && s.stageNum < F) next = s.stageNum + 1;
    if (next === s.stageNum) return s;
    return { ...s, stageNum: next };
  });
}
