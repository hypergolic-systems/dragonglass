// Parts catalog rune. PartCatalogTopic is a one-shot emission when
// the editor scene spins up, so the subscription just caches the
// first frame and keeps handing it out. Flight components never
// reach this code path; the topic isn't installed outside the VAB/
// SPH.

import { getKsp } from './context';
import { PartCatalogTopic } from '../core/topics';
import type { PartCatalogData } from '../core/part-catalog-data';

function defaults(): PartCatalogData {
  return { entries: [] };
}

/**
 * Subscribe to the parts catalog. Returns a reactive proxy whose
 * `entries` flips from `[]` to the full catalog on first server
 * frame. Re-entering the editor scene installs a fresh server
 * topic and re-emits, so `entries` never goes stale against a
 * version-mismatched KSP.
 *
 * Must be called during component initialization (needs Svelte
 * context and `$effect`).
 */
export function usePartCatalog(): PartCatalogData {
  const telemetry = getKsp();
  const data = $state<PartCatalogData>(defaults());

  $effect(() => {
    return telemetry.subscribe(PartCatalogTopic, (frame) => {
      // Frames are immutable per the readonly contract — swap the
      // whole entries array rather than mutating in place.
      (data as { entries: typeof frame.entries }).entries = frame.entries;
    });
  });

  return data;
}
