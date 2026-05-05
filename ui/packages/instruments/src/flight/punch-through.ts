// Punch-through stream registry.
//
// The HUD declares <PunchThrough id="…" chroma="…"> placeholders that
// the native plugin composites Unity-rendered textures (Kerbal IVA
// portraits, map insets, …) under via a chroma-key shader. Each
// placeholder paints a known chroma color in CSS; the plugin treats
// chroma pixels inside the placeholder rect as transparent and reveals
// the portrait beneath. Anything painted on top of the chroma fill
// (name plates, status pips, hover affordances) survives the key.
//
// This module owns the live registry of mounted placeholders. The
// pump component reads it once per `requestAnimationFrame` and ships
// the snapshot to the sidecar via `window.cefQuery`.

export const PUNCH_THROUGH_CONTEXT_KEY = Symbol('punch-through');

/** Default chroma color when a `<PunchThrough>` doesn't specify one.
 *  Pure magenta — debug-friendly (obvious if compositor fails) and
 *  unlikely to appear in HUD chrome. */
export const DEFAULT_CHROMA: readonly [number, number, number] = [255, 0, 255];

/** Default chroma threshold (max-channel distance, 0–255). */
export const DEFAULT_THRESHOLD = 24;

export interface PunchThroughEntry {
  /** Stream id. Hashed via FNV-1a (32-bit) on every side. */
  id: string;
  /** Bounding rect in CSS pixels, relative to the viewport. */
  rect: { x: number; y: number; w: number; h: number };
  /** Chroma color [r, g, b] (each 0–255). */
  chroma: readonly [number, number, number];
  /** Max-channel distance below which a CEF pixel is keyed (0–255). */
  threshold: number;
  /** True when the placeholder is currently visible (mounted + not
   *  fully clipped by an `IntersectionObserver`). */
  visible: boolean;
}

export interface PunchThroughRegistry {
  /** Register a placeholder. Returns an unregister function. */
  register(entry: PunchThroughEntry): () => void;
  /** Update an already-registered placeholder. */
  update(id: string, partial: Partial<Omit<PunchThroughEntry, 'id'>>): void;
  /** Snapshot of all currently-registered entries. */
  snapshot(): PunchThroughEntry[];
}

export function createPunchThroughRegistry(): PunchThroughRegistry {
  const entries = new Map<string, PunchThroughEntry>();

  return {
    register(entry) {
      entries.set(entry.id, entry);
      return () => {
        entries.delete(entry.id);
      };
    },
    update(id, partial) {
      const cur = entries.get(id);
      if (!cur) return;
      Object.assign(cur, partial);
    },
    snapshot() {
      // Cheap shallow copy of the values array. Per-frame allocation
      // is fine — 16-element ceiling, dwarfed by the JSON encode that
      // follows it.
      return Array.from(entries.values());
    },
  };
}
