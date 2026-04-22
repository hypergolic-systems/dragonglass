/**
 * Operations that mutate the active vessel's stage sequence via the
 * `stage` topic. Fire-and-forget — the server applies the mutation
 * on the Unity main thread on its next dispatch tick, then echoes
 * the updated sequence back on the next `stage` broadcast.
 *
 * Part identity uses `persistentId` (decimal string of KSP's
 * `Part.persistentId`) — matches the field shipped in `StagingPart`.
 * Stage identity uses KSP's own `stageNum` (lower = later in flight).
 */
export interface StageOps {
  /**
   * Move a part to a different existing stage. When `group` is
   * `true`, every symmetry cousin currently sharing the source
   * stage rides along — the whole "×N" group moves together.
   * `false` moves only the single named part; the cousins stay put.
   * Callers pick based on whether the user interacted with a
   * consolidated group icon (true) or an individual cousin revealed
   * by the client's "Ungroup" toggle (false).
   */
  movePart(
    persistentId: string,
    targetStageNum: number,
    group: boolean,
  ): void;

  /**
   * Create a new stage adjacent to the part's current one and move
   * the part (or the whole symmetry group, per `group`) into it.
   * `above` / `below` are visual senses in our UI: above = lower
   * stageNum; below = higher. Drag-drop never takes this path —
   * only the right-click menu does.
   */
  movePartToNewStage(
    persistentId: string,
    position: 'above' | 'below',
    group: boolean,
  ): void;

  /**
   * Reorder the stages themselves. Takes the stage at `fromStageNum`
   * and moves it to insertion position `insertPos` in the range
   * `[0, stages.length]`. Other stages shift around it automatically.
   *
   * The stage's final `stageNum` after the move is
   * `insertPos - 1` when `insertPos > fromStageNum` (removing it
   * first shifts the insertion index), else `insertPos`. No-op when
   * `insertPos` is `fromStageNum` or `fromStageNum + 1` (putting it
   * back where it was).
   */
  moveStage(fromStageNum: number, insertPos: number): void;

  /**
   * Highlight (or un-highlight) a part in the 3D scene. Mirrors the
   * stock stager's hover-to-highlight behaviour: pass a `persistentId`
   * to glow the matching part, or `null` to clear any current
   * highlight. The server tracks at most one highlighted part at a
   * time — the latest call wins.
   */
  setHighlightPart(persistentId: string | null): void;
}
