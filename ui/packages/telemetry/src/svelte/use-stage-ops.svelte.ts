import { getKsp } from './context';
import { StageTopic } from '../core/topics';
import type { StageOps } from '../core/stage-ops';

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
    setHighlightPart: (persistentId) =>
      telemetry.send(StageTopic, 'setHighlightPart', persistentId),
  };
}
