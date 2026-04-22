import { getKsp } from './context';
import { StageTopic } from '../core/topics';
import type { StageOps } from '../core/stage-ops';
import { applyMoveStage } from './use-stage.svelte';

/**
 * Typed handle for invoking stage-sequence mutations. Must be called
 * during component initialization (needs Svelte context). Returns a
 * stable object whose methods wrap `Ksp.send` so call sites read as
 * `ops.movePart(id, 3)` rather than the raw `telemetry.send(...)`.
 */
export function useStageOps(): StageOps {
  const telemetry = getKsp();
  return {
    movePart: (persistentId, targetStageNum, group) =>
      telemetry.send(StageTopic, 'movePart', persistentId, targetStageNum, group),
    movePartToNewStage: (persistentId, position, group) =>
      telemetry.send(StageTopic, 'movePartToNewStage', persistentId, position, group),
    moveStage: (fromStageNum, insertPos) => {
      telemetry.send(StageTopic, 'moveStage', fromStageNum, insertPos);
      // Optimistic local update — the server's echo will match so
      // no correction is visible; this just avoids a single-frame
      // flash of the pre-move state while we wait on the round
      // trip.
      applyMoveStage(fromStageNum, insertPos);
    },
    setHighlightParts: (persistentIds) =>
      telemetry.send(StageTopic, 'setHighlightParts', persistentIds),
  };
}
