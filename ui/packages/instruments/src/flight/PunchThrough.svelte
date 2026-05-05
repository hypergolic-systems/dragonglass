<script lang="ts">
  // Placeholder for a Unity-rendered surface composited under CEF via
  // chroma-key punch-through.
  //
  // On mount, registers the placeholder with the ambient
  // <PunchThroughProvider> context. The native plugin reads the
  // registered rect + chroma from SHM each render event and
  // composites the matching stream's portrait texture under CEF;
  // chroma pixels in the placeholder are keyed out by the shader so
  // the portrait reads through.
  //
  // The component renders a single div filled with the chroma color
  // and accepts a `<slot>` for overlay chrome (frame, name plate,
  // status pip, hover affordances) which sits *above* the chroma
  // fill in the same DOM order — so it composites over the portrait
  // naturally without any extra plumbing.

  import { getContext, onDestroy, onMount } from 'svelte';
  import {
    PUNCH_THROUGH_CONTEXT_KEY,
    DEFAULT_CHROMA,
    DEFAULT_THRESHOLD,
    type PunchThroughRegistry,
  } from './punch-through';

  let {
    id,
    chroma = DEFAULT_CHROMA,
    threshold = DEFAULT_THRESHOLD,
    children,
  }: {
    /** Stream id. Must match what the mod registered with the native
     *  plugin (`DgHud_PushStreamFrame(id, …)`). */
    id: string;
    /** Chroma color [r, g, b] (0–255 each). The plugin shader keys
     *  this color out of the rect to reveal the portrait beneath. */
    chroma?: readonly [number, number, number];
    /** Max-channel distance below which a CEF pixel is keyed (0–255). */
    threshold?: number;
    /** Optional overlay chrome rendered above the chroma fill. */
    children?: import('svelte').Snippet;
  } = $props();

  const registry = getContext<PunchThroughRegistry | null>(PUNCH_THROUGH_CONTEXT_KEY);

  let host = $state<HTMLDivElement | null>(null);
  let unregister: (() => void) | null = null;

  function readRect(el: HTMLElement) {
    const r = el.getBoundingClientRect();
    return {
      x: Math.round(r.left),
      y: Math.round(r.top),
      w: Math.round(r.width),
      h: Math.round(r.height),
    };
  }

  onMount(() => {
    if (!registry || !host) return;
    const initial = readRect(host);
    unregister = registry.register({
      id,
      rect: initial,
      chroma,
      threshold,
      visible: true,
    });

    // ResizeObserver fires whenever this element's size changes
    // (CSS layout shifts, parent reflows). Cheap; the registry
    // mutate is in-place.
    const ro = new ResizeObserver(() => {
      if (host) registry.update(id, { rect: readRect(host) });
    });
    ro.observe(host);

    // IntersectionObserver tracks visibility — the rAF pump skips
    // sending rects for hidden streams, so the plugin stops
    // compositing them.
    const io = new IntersectionObserver(
      (entries) => {
        for (const e of entries) {
          registry.update(id, { visible: e.isIntersecting });
        }
      },
      { threshold: 0 },
    );
    io.observe(host);

    // Position can also change without a size change (parent scroll,
    // sibling reflow). ResizeObserver doesn't fire on pure position
    // shifts, so re-read on every animation frame while mounted.
    // The registry compares by reference; per-frame allocation of a
    // 16-byte rect object is negligible.
    let raf = 0;
    const tick = () => {
      if (host) registry.update(id, { rect: readRect(host) });
      raf = requestAnimationFrame(tick);
    };
    raf = requestAnimationFrame(tick);

    return () => {
      ro.disconnect();
      io.disconnect();
      cancelAnimationFrame(raf);
    };
  });

  onDestroy(() => {
    unregister?.();
    unregister = null;
  });

  // Reactive chroma update: if the prop changes, push it to the
  // registry so the plugin re-keys with the new color on its next
  // render event.
  $effect(() => {
    if (!registry) return;
    registry.update(id, { chroma, threshold });
  });

  const chromaRgb = $derived(`rgb(${chroma[0]}, ${chroma[1]}, ${chroma[2]})`);
</script>

<div
  bind:this={host}
  class="punch-through"
  style:background-color={chromaRgb}
  data-punch-id={id}
>
  {@render children?.()}
</div>

<style>
  .punch-through {
    /* The placeholder paints the chroma color as a solid background.
       The plugin's chroma-key shader sees these pixels in the CEF
       surface and reveals the portrait at this rect; anything in
       the slot composites above naturally. */
    position: relative;
    width: 100%;
    height: 100%;
    overflow: hidden;
  }
</style>
