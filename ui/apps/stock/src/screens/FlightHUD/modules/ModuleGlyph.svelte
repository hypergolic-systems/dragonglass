<script lang="ts">
  // Decorative iconograph per module kind. Rendered as a large (38px)
  // low-opacity stamp in the top-right of each module section —
  // behind the readouts — so the eye gets a per-module identity cue
  // without the glyph competing with live values. Pure SVG, 1.5-stroke
  // phosphor-green outlines; sized and coloured via the surrounding
  // .m-stamp CSS class, so callers don't need to pass color props.
  //
  // Each glyph is an abstraction of the module's function, not a
  // literal stock-KSP icon. Stock already has a full iconography we
  // couldn't reproduce on a 20×20 viewBox anyway; the goal here is
  // typographic-level identity — the player recognises "oh, solar"
  // before they read the label.

  import type { PartModuleData } from '@dragonglass/telemetry/core';

  type Kind = PartModuleData['kind'];

  interface Props {
    kind: Kind;
  }
  const { kind }: Props = $props();
</script>

<svg class="m-stamp" viewBox="0 0 20 20" aria-hidden="true">
  {#if kind === 'engines'}
    <!-- Thrust bell + flame plume -->
    <path d="M 7 3 L 13 3 L 12 10 L 14 13 L 6 13 L 8 10 Z" />
    <path d="M 8 14 L 10 18 L 12 14" stroke-dasharray="1 1" />
  {:else if kind === 'sensor'}
    <!-- Analog meter arc + needle -->
    <path d="M 3 14 A 7 7 0 0 1 17 14" />
    <line x1="10" y1="14" x2="14" y2="7" />
    <circle cx="10" cy="14" r="1.2" fill="currentColor" />
  {:else if kind === 'science'}
    <!-- Erlenmeyer flask -->
    <path d="M 8 3 L 8 8 L 4 16 L 16 16 L 12 8 L 12 3 Z" />
    <line x1="7" y1="3" x2="13" y2="3" />
    <line x1="6" y1="12" x2="14" y2="12" opacity="0.5" />
  {:else if kind === 'solar'}
    <!-- Sun disc with rays -->
    <circle cx="10" cy="10" r="3.5" />
    <line x1="10" y1="2" x2="10" y2="4" />
    <line x1="10" y1="16" x2="10" y2="18" />
    <line x1="2" y1="10" x2="4" y2="10" />
    <line x1="16" y1="10" x2="18" y2="10" />
    <line x1="4.3" y1="4.3" x2="5.7" y2="5.7" />
    <line x1="14.3" y1="14.3" x2="15.7" y2="15.7" />
    <line x1="4.3" y1="15.7" x2="5.7" y2="14.3" />
    <line x1="14.3" y1="5.7" x2="15.7" y2="4.3" />
  {:else if kind === 'generator'}
    <!-- Lightning bolt -->
    <path d="M 11 2 L 5 11 L 9 11 L 7 18 L 15 8 L 11 8 Z" />
  {:else if kind === 'light'}
    <!-- Bulb -->
    <path d="M 7 12 A 4 4 0 1 1 13 12 L 13 14 L 7 14 Z" />
    <line x1="8" y1="15.5" x2="12" y2="15.5" />
    <line x1="9" y1="17" x2="11" y2="17" />
  {:else if kind === 'parachute'}
    <!-- Dome + shroud lines -->
    <path d="M 3 10 A 7 7 0 0 1 17 10 L 17 10 L 3 10 Z" />
    <line x1="4" y1="10" x2="8" y2="17" />
    <line x1="10" y1="10" x2="10" y2="17" />
    <line x1="16" y1="10" x2="12" y2="17" />
  {:else if kind === 'command'}
    <!-- Cockpit reticle -->
    <circle cx="10" cy="10" r="7" />
    <line x1="10" y1="3" x2="10" y2="7" />
    <line x1="10" y1="13" x2="10" y2="17" />
    <line x1="3" y1="10" x2="7" y2="10" />
    <line x1="13" y1="10" x2="17" y2="10" />
    <circle cx="10" cy="10" r="1.2" fill="currentColor" />
  {:else if kind === 'reactionWheel'}
    <!-- Three circular arrows / gyro -->
    <circle cx="10" cy="10" r="3" />
    <path d="M 10 3 A 7 7 0 0 1 16.5 13" />
    <path d="M 7 17 A 7 7 0 0 1 3.5 7" />
    <path d="M 14 3 L 16.5 5 L 14 7" />
    <path d="M 6 17 L 3.5 15 L 6 13" />
  {:else if kind === 'rcs'}
    <!-- 4-axis cross with thruster bells -->
    <line x1="10" y1="3" x2="10" y2="17" />
    <line x1="3" y1="10" x2="17" y2="10" />
    <path d="M 8 3 L 10 1 L 12 3" />
    <path d="M 8 17 L 10 19 L 12 17" />
    <path d="M 3 8 L 1 10 L 3 12" />
    <path d="M 17 8 L 19 10 L 17 12" />
  {:else if kind === 'decoupler'}
    <!-- Separation — two stacked plates pushed apart -->
    <line x1="3" y1="7" x2="17" y2="7" />
    <line x1="3" y1="13" x2="17" y2="13" />
    <path d="M 7 4 L 10 1 L 13 4" />
    <path d="M 7 16 L 10 19 L 13 16" />
  {:else if kind === 'transmitter'}
    <!-- Dish + signal arcs -->
    <path d="M 4 14 L 10 3 L 16 14 Z" />
    <path d="M 13 8 A 3 3 0 0 1 16 5" opacity="0.6" />
    <path d="M 14 10 A 5 5 0 0 1 19 6" opacity="0.4" />
  {:else if kind === 'deployAntenna'}
    <!-- Mesh dish + support strut -->
    <path d="M 3 8 L 17 8 L 14 14 L 6 14 Z" />
    <line x1="6" y1="8" x2="8" y2="14" opacity="0.5" />
    <line x1="10" y1="8" x2="10" y2="14" opacity="0.5" />
    <line x1="14" y1="8" x2="12" y2="14" opacity="0.5" />
    <line x1="10" y1="14" x2="10" y2="18" />
  {:else if kind === 'deployRadiator'}
    <!-- Radiator fin array -->
    <rect x="3" y="4" width="3" height="12" />
    <rect x="8.5" y="4" width="3" height="12" />
    <rect x="14" y="4" width="3" height="12" />
  {:else if kind === 'activeRadiator'}
    <!-- Heat waves + fin -->
    <rect x="5" y="7" width="10" height="6" />
    <path d="M 4 4 Q 5 5 4 6" opacity="0.7" />
    <path d="M 8 4 Q 9 5 8 6" opacity="0.7" />
    <path d="M 12 4 Q 13 5 12 6" opacity="0.7" />
    <path d="M 4 14 Q 5 15 4 16" opacity="0.7" />
    <path d="M 8 14 Q 9 15 8 16" opacity="0.7" />
    <path d="M 12 14 Q 13 15 12 16" opacity="0.7" />
  {:else if kind === 'harvester'}
    <!-- Drill bit with flutes -->
    <path d="M 10 2 L 14 8 L 14 14 L 10 18 L 6 14 L 6 8 Z" />
    <line x1="6" y1="10" x2="14" y2="10" opacity="0.5" />
    <line x1="6" y1="13" x2="14" y2="13" opacity="0.5" />
  {:else if kind === 'converter'}
    <!-- Gear -->
    <circle cx="10" cy="10" r="4" />
    <circle cx="10" cy="10" r="1.5" fill="currentColor" />
    <line x1="10" y1="2" x2="10" y2="4" />
    <line x1="10" y1="16" x2="10" y2="18" />
    <line x1="2" y1="10" x2="4" y2="10" />
    <line x1="16" y1="10" x2="18" y2="10" />
    <line x1="4.5" y1="4.5" x2="5.9" y2="5.9" />
    <line x1="14.1" y1="14.1" x2="15.5" y2="15.5" />
    <line x1="4.5" y1="15.5" x2="5.9" y2="14.1" />
    <line x1="14.1" y1="5.9" x2="15.5" y2="4.5" />
  {:else if kind === 'controlSurface'}
    <!-- Airfoil silhouette -->
    <path d="M 2 10 Q 6 6 11 7 Q 15 8 18 10 Q 15 11 11 11 Q 6 11 2 10 Z" />
    <line x1="13" y1="9" x2="15" y2="12" opacity="0.6" />
  {:else if kind === 'alternator'}
    <!-- Spinning rotor -->
    <circle cx="10" cy="10" r="6" />
    <path d="M 10 4 L 14 10 L 10 16 L 6 10 Z" fill="currentColor" opacity="0.3" />
    <circle cx="10" cy="10" r="1" fill="currentColor" />
  {:else}
    <!-- Generic fallback — module ID block -->
    <rect x="4" y="4" width="12" height="12" />
    <line x1="4" y1="8" x2="16" y2="8" opacity="0.5" />
    <line x1="4" y1="12" x2="16" y2="12" opacity="0.5" />
  {/if}
</svg>

<style>
  .m-stamp {
    position: absolute;
    /* Offset below the top edge of the module so the stamp doesn't
       collide with each module's NAME label + badge (both live at
       the top). Anchoring right-of-center means the stamp reads as
       a phosphor "department mark" stamped onto the page, rather
       than as decoration competing with the header. */
    top: 26px;
    right: -6px;
    width: 52px;
    height: 52px;
    pointer-events: none;
    color: var(--accent);
    /* Still deliberately low. The stamp is a texture element — if
       the player has to read it, it's too loud. 0.12 puts it just
       at the threshold of peripheral awareness against the dark
       panel. */
    opacity: 0.12;
    filter: drop-shadow(0 0 4px var(--accent-glow));
    z-index: 0;
  }
  .m-stamp :global(path),
  .m-stamp :global(line),
  .m-stamp :global(rect),
  .m-stamp :global(circle:not([fill='currentColor'])) {
    fill: none;
    stroke: currentColor;
    stroke-width: 1.3;
    stroke-linecap: round;
    stroke-linejoin: round;
  }
</style>
