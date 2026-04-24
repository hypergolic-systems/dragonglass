import { getKsp } from './context';
import { PartCatalogTopic } from '../core/topics';
import type { PartCatalogOps } from '../core/part-catalog-data';

/**
 * Typed handle for invoking catalog-side ops (today: `pickPart`).
 * Must be called during component initialization (needs Svelte
 * context). Returns a stable object whose methods wrap `Ksp.send` so
 * call sites read as `ops.pickPart('liquidEngine1')`.
 */
export function usePartCatalogOps(): PartCatalogOps {
  const telemetry = getKsp();
  return {
    pickPart: (partName) =>
      telemetry.send(PartCatalogTopic, 'pickPart', partName),
  };
}
