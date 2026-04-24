// Shared RAF driver for push-mode `Smoothed` instances.
//
// One process-wide registry, one `requestAnimationFrame` loop. Each
// `Smoothed` registers when it gains its first push subscriber and
// unregisters when it loses its last; the loop only runs while the
// active set is non-empty, so a UI with no smoothed-and-subscribed
// values pays nothing per frame.
//
// Why not one RAF per `Smoothed`? Dozens of values can be on screen
// at once (one per visible part-action-window anchor, one per
// instrument needle, ...). Each `requestAnimationFrame` registration
// has its own dispatch overhead and doesn't share its tick budget
// with siblings. Single-loop dispatch keeps the per-tick cost
// linear in the number of smoothers and keeps them all observing the
// same `t`, which avoids subtle "instrument A advanced one frame
// but instrument B didn't" stutter.
//
// Pull-mode `sample()` callers don't need the registry at all — they
// drive their own loop (e.g. threlte's `useTask`).

interface Tickable {
  tick(t: number): void;
}

class Registry {
  private readonly active = new Set<Tickable>();
  private rafId: number | null = null;

  add(s: Tickable): void {
    this.active.add(s);
    if (this.rafId === null) this.start();
  }

  remove(s: Tickable): void {
    this.active.delete(s);
    if (this.active.size === 0 && this.rafId !== null) this.stop();
  }

  private start(): void {
    // Guarded against SSR — registry creation happens at module-load
    // in code that may transitively import on the server. Only start
    // the loop when we actually have a window to schedule against.
    if (typeof requestAnimationFrame !== 'function') return;
    const loop = (now: number) => {
      const t = now / 1000;
      // Snapshot under a defensive copy so a subscriber unsubscribing
      // mid-tick (and thus mutating `active`) doesn't break iteration.
      const snapshot = Array.from(this.active);
      for (let i = 0; i < snapshot.length; i++) {
        snapshot[i].tick(t);
      }
      // Re-arm only if anyone's still listening. A subscriber that
      // unsubscribed during this tick may have left the set empty.
      if (this.active.size > 0) {
        this.rafId = requestAnimationFrame(loop);
      } else {
        this.rafId = null;
      }
    };
    this.rafId = requestAnimationFrame(loop);
  }

  private stop(): void {
    if (this.rafId !== null && typeof cancelAnimationFrame === 'function') {
      cancelAnimationFrame(this.rafId);
    }
    this.rafId = null;
  }
}

export const SmoothedRegistry = new Registry();
