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
    type PunchThroughRegistry,
  } from './punch-through';

  let {
    id,
    chroma = DEFAULT_CHROMA,
    children,
  }: {
    /** Stream id. Must match what the mod registered with the native
     *  plugin (`DgHud_PushStreamFrame(id, …)`). */
    id: string;
    /** Chroma color [r, g, b] (0–255 each). Used as the CSS placeholder
     *  fill so the user sees a visible cue when the compositor isn't
     *  running. The encoded-row pipeline doesn't chroma-key — the
     *  shader samples the portrait at rect-local UV. */
    chroma?: readonly [number, number, number];
    /** Optional overlay chrome rendered above the chroma fill. */
    children?: import('svelte').Snippet;
  } = $props();

  const registry = getContext<PunchThroughRegistry | null>(PUNCH_THROUGH_CONTEXT_KEY);

  let host = $state<HTMLDivElement | null>(null);
  let unregister: (() => void) | null = null;

  onMount(() => {
    if (!registry || !host) return;
    unregister = registry.register({
      id,
      el: host,
      chroma,
      visible: true,
    });

    // IntersectionObserver tracks visibility — the encoder skips
    // hidden streams so the plugin stops compositing them. We don't
    // need ResizeObserver or a per-element rAF anymore: the encoder
    // reads `getBoundingClientRect()` fresh inside its own rAF tick,
    // so any size or position change shows up the same frame it
    // happens.
    const io = new IntersectionObserver(
      (entries) => {
        for (const e of entries) {
          registry.update(id, { visible: e.isIntersecting });
        }
      },
      { threshold: 0 },
    );
    io.observe(host);

    return () => {
      io.disconnect();
    };
  });

  onDestroy(() => {
    unregister?.();
    unregister = null;
  });

  $effect(() => {
    if (!registry) return;
    registry.update(id, { chroma });
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
