<script lang="ts">
  // Tiny engine-layout icon — one per fuel group in the Propulsion
  // panel. Receives the same `EngineMapLayout` the main rosette is
  // drawn from, so positions read identically across both instruments
  // (and any caller-side filter like "hide unactivated engines"
  // applies to both at once). Engines belonging to the highlighted
  // group render as filled teal discs; everyone else renders as
  // thin muted outlines, so the icon answers "which engines are in
  // this group?" at a glance without repeating the full rosette.

  import type { EngineMapLayout } from './engine-map';

  let {
    layout,
    groupIds,
  }: {
    layout: EngineMapLayout;
    groupIds: readonly string[];
  } = $props();

  const inGroup = $derived(new Set(groupIds));
</script>

<svg
  class="engine-icon"
  viewBox="-1 -1 2 2"
  preserveAspectRatio="xMidYMid meet"
  aria-hidden="true"
>
  {#each layout.points as p (p.id)}
    <!-- Radius encodes thrust — it stays the same whether or not
         the engine is in the highlighted group. Only the fill /
         stroke toggles, so the layout reads identically across
         sibling icons for different groups. -->
    <circle
      class="engine-icon__dot"
      class:engine-icon__dot--active={inGroup.has(p.id)}
      cx={p.cx}
      cy={p.cy}
      r={p.r}
    />
  {/each}
</svg>

<style>
  .engine-icon {
    display: block;
    width: 22px;
    height: 22px;
    flex: 0 0 auto;
    overflow: visible;
  }

  .engine-icon__dot {
    fill: none;
    stroke: var(--fg-mute);
    stroke-width: 0.06;
    transition: fill 220ms ease, stroke 220ms ease;
  }

  .engine-icon__dot--active {
    fill: var(--accent);
    stroke: var(--accent);
    stroke-width: 0.06;
    filter: drop-shadow(0 0 1px var(--accent-glow));
  }
</style>
