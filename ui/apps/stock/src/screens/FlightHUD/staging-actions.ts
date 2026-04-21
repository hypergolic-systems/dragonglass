// Menu-item builders for the staging stack's right-click menus.
//
// Pure functions — no Svelte, no DOM. Operate on a `PartRenderItem`
// (a single icon's rendering context, carrying its part identity plus
// symmetry-group state) and return a `MenuItem[]` the
// `ContextMenu.svelte` component can consume directly.
//
// Terminology. KSP's stage numbers run backwards — stage 0 fires
// last, higher stageNums fire earlier. In our UI lower stageNum is at
// the visual top; "Move up" in the labels below means "decrease
// stageNum" = "toward the top of the stack".

import type {
  StageEntry,
  StageOps,
  StagingPart,
} from '@dragonglass/telemetry/core';
import type { MenuItem } from './ContextMenu.svelte';

/** A single icon in the rendered stage card, with whatever context
 *  its right-click menu / drag handler needs. Built by
 *  `expandStageParts` (see `stage-render.ts`). */
export interface PartRenderItem {
  /** What the StagingIcon shows. For cousins revealed by "Ungroup",
   *  this is a synthesised part copying the representative's kind /
   *  iconName but using the cousin's own persistentId and an empty
   *  cousinsInStage list. */
  readonly part: StagingPart;
  /** The persistentId used as the key in the ungroup Set for this
   *  symmetry group. `null` for singleton parts that aren't in any
   *  group. Cousin icons share the representative's id here so
   *  clicking Regroup on any of them collapses the whole set. */
  readonly groupRepId: string | null;
  /** True if this icon is rendered as the consolidated "×N"
   *  representative (grouped view). False for singletons and for
   *  individual cousins in the ungrouped view. Drives move-op
   *  `group` flags and which symmetry action the menu offers. */
  readonly isConsolidated: boolean;
  /** Multiplicity for the badge. 1 for non-consolidated / singleton
   *  items; `>= 2` on a consolidated group icon. */
  readonly count: number;
}

/**
 * Menu for a right-click on a single staged-part icon. Handles both
 * regular move ops and the symmetry toggle (Ungroup ↔ Regroup).
 *
 * `stages` must be the full current list from `StageData.stages` so
 * we can tell whether adjacent stages exist (to enable/disable
 * "Move up" / "Move down").
 */
export function buildPartMenu(
  item: PartRenderItem,
  currentStageNum: number,
  stages: readonly StageEntry[],
  ops: StageOps,
  toggleUngroup: (repId: string) => void,
  isUngrouped: (repId: string) => boolean,
): MenuItem[] {
  // Unique stageNums in visual order (ascending).
  const nums = [...new Set(stages.map((s) => s.stageNum))].sort((a, b) => a - b);
  const idx = nums.indexOf(currentStageNum);
  const upTarget = idx > 0 ? nums[idx - 1] : null;
  const downTarget = idx >= 0 && idx < nums.length - 1 ? nums[idx + 1] : null;

  const partId = item.part.persistentId;
  const group = item.isConsolidated;

  const items: MenuItem[] = [
    {
      label: 'Move up',
      disabled: upTarget === null,
      onSelect: () => {
        if (upTarget !== null) ops.movePart(partId, upTarget, group);
      },
    },
    {
      label: 'Move down',
      disabled: downTarget === null,
      onSelect: () => {
        if (downTarget !== null) ops.movePart(partId, downTarget, group);
      },
    },
    {
      label: 'Move to new stage above',
      onSelect: () => ops.movePartToNewStage(partId, 'above', group),
    },
    {
      label: 'Move to new stage below',
      onSelect: () => ops.movePartToNewStage(partId, 'below', group),
    },
  ];

  // Ungroup / Regroup toggle. Only visible for parts that are
  // physically in a symmetry group. The toggle is client-only — it
  // doesn't touch KSP state, it just flips how we render the icons
  // for this group.
  if (item.groupRepId !== null) {
    const repId = item.groupRepId;
    if (isUngrouped(repId)) {
      items.push({
        label: 'Regroup',
        onSelect: () => toggleUngroup(repId),
      });
    } else if (item.count > 1) {
      items.push({
        label: `Ungroup ×${item.count}`,
        onSelect: () => toggleUngroup(repId),
      });
    }
  }

  return items;
}
