// Given a stage's parts and the set of representatives the user has
// "Ungrouped" in the UI, produce the render items for that stage.
//
// Three cases per input StagingPart:
//
//   1. Singleton (cousinsInStage empty). One render item with
//      groupRepId = null, count = 1. No symmetry machinery.
//
//   2. Consolidated group (cousinsInStage non-empty AND representative
//      NOT in the ungrouped set). One render item carrying the
//      `×N` badge; moves propagate to the whole group.
//
//   3. Expanded group (cousinsInStage non-empty AND representative IS
//      in the ungrouped set). N render items — one for the
//      representative, one per cousin in cousinsInStage. Each carries
//      count = 1 and groupRepId = representative.persistentId. The
//      representative's item gets the original StagingPart unchanged;
//      cousin items are synthesised with the cousin's own persistentId
//      and an empty cousinsInStage.

import type { StagingPart } from '@dragonglass/telemetry/core';
import type { PartRenderItem } from './staging-actions';

export function expandStageParts(
  parts: readonly StagingPart[],
  ungrouped: ReadonlySet<string>,
): PartRenderItem[] {
  const out: PartRenderItem[] = [];
  for (const p of parts) {
    if (p.cousinsInStage.length === 0) {
      out.push({
        part: p,
        groupRepId: null,
        isConsolidated: false,
        count: 1,
      });
      continue;
    }

    const count = p.cousinsInStage.length + 1;
    if (!ungrouped.has(p.persistentId)) {
      // Consolidated view — one icon with the ×N badge.
      out.push({
        part: p,
        groupRepId: p.persistentId,
        isConsolidated: true,
        count,
      });
      continue;
    }

    // Expanded view — representative + one phantom per cousin.
    out.push({
      part: p,
      groupRepId: p.persistentId,
      isConsolidated: false,
      count: 1,
    });
    for (const cousinId of p.cousinsInStage) {
      out.push({
        part: {
          kind: p.kind,
          persistentId: cousinId,
          iconName: p.iconName,
          cousinsInStage: [],
        },
        groupRepId: p.persistentId,
        isConsolidated: false,
        count: 1,
      });
    }
  }
  return out;
}
