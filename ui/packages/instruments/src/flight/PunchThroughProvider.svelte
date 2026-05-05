<script lang="ts">
  // Punch-through provider + pump.
  //
  // Provides the registry context to descendant <PunchThrough>
  // components and runs a `requestAnimationFrame` loop that snapshots
  // the registry and ships it to the sidecar via `window.cefQuery`.
  //
  // The native plugin reads the SHM stream-rect table the sidecar
  // wrote and chroma-keys the CEF surface inside each rect — so a
  // mounted <PunchThrough id="kerbal:Jeb"> ends up showing whatever
  // texture the mod registered under "kerbal:Jeb".
  //
  // Mount once, near the root of the HUD. One per CEF browser.

  import { onDestroy, onMount, setContext } from 'svelte';
  import {
    PUNCH_THROUGH_CONTEXT_KEY,
    createPunchThroughRegistry,
    type PunchThroughEntry,
  } from './punch-through';

  let {
    children,
  }: {
    children?: import('svelte').Snippet;
  } = $props();

  const registry = createPunchThroughRegistry();
  setContext(PUNCH_THROUGH_CONTEXT_KEY, registry);

  type DgUpdatePunchRectsFn = (json: string) => void;

  function getNativeBinding(): DgUpdatePunchRectsFn | null {
    // Bound to `window` by the sidecar's `KspRenderProcessHandler`
    // on every new V8 context. Undefined when running in the dev
    // server (vanilla browser) — the pump no-ops in that case.
    const w = window as unknown as { dgUpdatePunchRects?: DgUpdatePunchRectsFn };
    return typeof w.dgUpdatePunchRects === 'function' ? w.dgUpdatePunchRects : null;
  }

  function pack(entries: PunchThroughEntry[]): string {
    // Drop hidden / zero-size entries before serialising. Keeps the
    // payload small and prevents the plugin from compositing for
    // stream slots whose UI element is offscreen.
    const visible = entries.filter(
      (e) => e.visible && e.rect.w > 0 && e.rect.h > 0,
    );
    const dpr = window.devicePixelRatio || 1;
    return JSON.stringify({
      rects: visible.map((e) => ({
        id: e.id,
        x: Math.round(e.rect.x * dpr),
        y: Math.round(e.rect.y * dpr),
        w: Math.round(e.rect.w * dpr),
        h: Math.round(e.rect.h * dpr),
        chroma: [e.chroma[0], e.chroma[1], e.chroma[2]],
        threshold: e.threshold,
      })),
    });
  }

  let lastSent = '';
  let raf = 0;

  onMount(() => {
    const update = getNativeBinding();
    if (!update) {
      console.info(
        '[punch-through] window.dgUpdatePunchRects unavailable — pump idle',
      );
      return;
    }

    const tick = () => {
      raf = requestAnimationFrame(tick);
      const snapshot = registry.snapshot();
      const payload = pack(snapshot);
      // Skip the round-trip when nothing changed. JSON-equality is a
      // good enough proxy: rects round to integer DPR pixels, so
      // sub-pixel CSS animations don't generate spurious traffic.
      if (payload === lastSent) return;
      lastSent = payload;
      try {
        update(payload);
      } catch (e) {
        console.warn(`[punch-through] dgUpdatePunchRects threw: ${e}`);
      }
    };
    raf = requestAnimationFrame(tick);
  });

  onDestroy(() => {
    if (raf) cancelAnimationFrame(raf);
    raf = 0;
  });
</script>

{@render children?.()}
