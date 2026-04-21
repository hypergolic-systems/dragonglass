<script lang="ts">
  // Glyph for a single staged part. `iconName` is KSP's
  // `DefaultIcons` enum name as it appears in `Part.stagingIcon`
  // (e.g. "LIQUID_ENGINE", "DECOUPLER_VERT"); we render a procedural
  // SVG approximation per known name, matching the inline-SVG /
  // procedurally-drawn icon convention used by the rest of the HUD
  // (Navball reticle, EngineIcon, Propulsion rosette).
  //
  // The glyphs are not pixel-exact copies of KSP's atlas icons —
  // they're shape families that read at a glance as
  // "bell / cylinder / chute / etc." at ~12-14 px. Higher fidelity
  // (extracting KSP's atlas, or hand-tuned SVGs) can replace the
  // switch below without changing consumers.
  //
  // `persistentId` is forwarded onto the root element as
  // `data-part-id` so future hover / drag-between-stages handlers
  // can pick the part out of a parent delegated listener without
  // prop drilling.

  import type { StagingPartKind } from '@dragonglass/telemetry/core';

  let {
    iconName,
    kind,
    persistentId,
    symmetryCount = 1,
    oncontext,
    onhover,
    ondragstart,
  }: {
    iconName: string;
    kind: StagingPartKind;
    persistentId: string;
    /** Number of physically-symmetric parts this icon represents.
     *  When > 1, the glyph shows a small "×N" badge in the
     *  bottom-right corner. */
    symmetryCount?: number;
    /** Right-click / ContextMenu-key handler. Called with the
     *  triggering event so the parent can read client coords and
     *  open a positioned menu. */
    oncontext?: (e: MouseEvent | KeyboardEvent) => void;
    /** Hover-enter / hover-leave bridge. Fires with the part's
     *  persistentId on enter, `null` on leave. The parent typically
     *  pipes this straight into `StageOps.setHighlightPart` so the
     *  matching 3D part glows while the cursor is over the glyph —
     *  mirrors stock KSP's hover-to-highlight stager behaviour. */
    onhover?: (persistentId: string | null) => void;
    /** Left-button pointerdown. The parent decides whether this
     *  becomes a drag (tentative until the cursor moves past a
     *  threshold) vs. a no-op click. */
    ondragstart?: (e: PointerEvent) => void;
  } = $props();

  function onKey(e: KeyboardEvent): void {
    // The ContextMenu key + Shift+F10 are the keyboard parity for
    // right-click. Enter/Space are reserved for future primary
    // actions (e.g. activate the part) so we stay keyboard-clean.
    if (e.key === 'ContextMenu' || (e.shiftKey && e.key === 'F10')) {
      e.preventDefault();
      oncontext?.(e);
    }
  }

  // Known glyph names; unknown falls through to the boxed-? fallback
  // and gets logged once per name so the gap shows up in dev without
  // spamming the console on every frame.
  const KNOWN = new Set([
    'LIQUID_ENGINE',
    'SOLID_BOOSTER',
    'SRB',
    'DECOUPLER_VERT',
    'DECOUPLER_HOR',
    'FUEL_TANK',
    'PARACHUTES',
    'RCS_MODULE',
    'COMMAND_POD',
    'ANTENNA',
    'STRUT',
  ]);
  const warned = new Set<string>();
  $effect(() => {
    if (!KNOWN.has(iconName) && !warned.has(iconName)) {
      warned.add(iconName);
      console.warn(`[dragonglass] StagingIcon: no glyph for "${iconName}" — using fallback`);
    }
  });
</script>

{#if oncontext || onhover || ondragstart}
  <!-- Interactive wrapper: native <button> so focus, keyboard and
       click semantics are what the browser expects — avoids the
       a11y footguns and CEF event-routing quirks of tab-indexed
       SVGs. The SVG is purely presentational inside. -->
  <button
    type="button"
    class="staging-icon-btn staging-icon-btn--{kind}"
    class:staging-icon-btn--symmetric={symmetryCount > 1}
    data-part-id={persistentId}
    aria-label={symmetryCount > 1 ? `${iconName} ×${symmetryCount}` : iconName}
    onclick={(e) => {
      // Swallow left-clicks so focus moves but the page can't
      // navigate anywhere / submit any form.
      e.preventDefault();
    }}
    oncontextmenu={oncontext ? (e) => {
      e.preventDefault();
      e.stopPropagation();
      oncontext(e);
    } : undefined}
    onkeydown={oncontext ? onKey : undefined}
    onmouseenter={onhover ? () => onhover(persistentId) : undefined}
    onmouseleave={onhover ? () => onhover(null) : undefined}
    onfocus={onhover ? () => onhover(persistentId) : undefined}
    onblur={onhover ? () => onhover(null) : undefined}
    onpointerdown={ondragstart ? (e) => {
      if (e.button === 0) ondragstart(e);
    } : undefined}
  >
    {@render glyph()}
    {#if symmetryCount > 1}
      <span class="staging-icon-btn__count" aria-hidden="true">×{symmetryCount}</span>
    {/if}
  </button>
{:else}
  {@render glyph()}
{/if}

{#snippet glyph()}
<svg
  class="staging-icon staging-icon--{kind}"
  viewBox="0 0 20 20"
  aria-hidden="true"
>
  {#if iconName === 'LIQUID_ENGINE'}
    <!-- Bell nozzle: thick upper section tapering outward to the
         exit plane; two fins hint at a gimballed liquid engine. -->
    <path d="M 7 4 H 13 V 9 L 16 16 H 4 L 7 9 Z" />
    <line x1="10" y1="4" x2="10" y2="9" class="staging-icon__accent" />
  {:else if iconName === 'SOLID_BOOSTER' || iconName === 'SRB'}
    <!-- Solid-rocket cylinder with the distinct fuel-grain cross
         hatching at the top. Filled to separate it from the hollow
         liquid-engine silhouette. -->
    <rect x="6" y="3" width="8" height="13" rx="0.5" class="staging-icon__filled" />
    <line x1="6" y1="6" x2="14" y2="6" class="staging-icon__accent-light" />
    <line x1="6" y1="9" x2="14" y2="9" class="staging-icon__accent-light" />
  {:else if iconName === 'DECOUPLER_VERT'}
    <!-- Two stacked blocks with a jagged separator — decoupler in a
         vertical stack. -->
    <rect x="4" y="3" width="12" height="5" />
    <rect x="4" y="12" width="12" height="5" />
    <path d="M 4 10 L 7 10 L 8 9 L 10 10 L 12 9 L 13 10 L 16 10" class="staging-icon__accent" />
  {:else if iconName === 'DECOUPLER_HOR'}
    <!-- Side-by-side blocks with a jagged vertical separator. -->
    <rect x="3" y="4" width="5" height="12" />
    <rect x="12" y="4" width="5" height="12" />
    <path d="M 10 4 L 10 7 L 9 8 L 10 10 L 9 12 L 10 13 L 10 16" class="staging-icon__accent" />
  {:else if iconName === 'FUEL_TANK'}
    <!-- Capsule-shaped tank with horizontal band for readability. -->
    <rect x="5" y="3" width="10" height="14" rx="1.5" />
    <line x1="5" y1="10" x2="15" y2="10" class="staging-icon__accent-light" />
  {:else if iconName === 'PARACHUTES'}
    <!-- Canopy arc + lines converging to the payload. -->
    <path d="M 3 9 Q 10 2 17 9" />
    <line x1="5" y1="9" x2="9.5" y2="16" />
    <line x1="10" y1="9" x2="10" y2="16" />
    <line x1="15" y1="9" x2="10.5" y2="16" />
  {:else if iconName === 'RCS_MODULE'}
    <!-- Four-way thruster cluster. -->
    <circle cx="10" cy="10" r="2.5" class="staging-icon__filled" />
    <line x1="10" y1="3" x2="10" y2="7" />
    <line x1="10" y1="13" x2="10" y2="17" />
    <line x1="3" y1="10" x2="7" y2="10" />
    <line x1="13" y1="10" x2="17" y2="10" />
  {:else if iconName === 'COMMAND_POD'}
    <!-- Capsule trapezoid with a window slit. -->
    <path d="M 7 3 H 13 L 16 16 H 4 Z" />
    <line x1="7.5" y1="7" x2="12.5" y2="7" class="staging-icon__accent" />
  {:else if iconName === 'ANTENNA'}
    <!-- Dish silhouette. -->
    <path d="M 4 14 Q 10 4 16 14" />
    <line x1="10" y1="9" x2="10" y2="16" />
  {:else if iconName === 'STRUT'}
    <!-- Diagonal strut. -->
    <line x1="3" y1="17" x2="17" y2="3" />
    <circle cx="3" cy="17" r="1.5" class="staging-icon__filled" />
    <circle cx="17" cy="3" r="1.5" class="staging-icon__filled" />
  {:else}
    <!-- Fallback: a boxed question mark so unknown icon names stay
         visible and the gap is obvious in review. The component
         also logs once per unknown name via the $effect above. -->
    <rect x="4" y="4" width="12" height="12" rx="1.5" class="staging-icon__fallback" />
    <text x="10" y="14" text-anchor="middle" class="staging-icon__fallback-text">?</text>
  {/if}
</svg>
{/snippet}

<style>
  .staging-icon {
    display: block;
    width: 14px;
    height: 14px;
    flex: 0 0 auto;
    overflow: visible;
    color: var(--fg-dim);
    stroke: currentColor;
    stroke-width: 1.1;
    fill: none;
    stroke-linecap: round;
    stroke-linejoin: round;
  }

  /* Kind-keyed tints so a glance at the column tells engine rows
     from decoupler rows from chute rows. */
  .staging-icon--engine {
    color: var(--accent);
  }
  .staging-icon--decoupler {
    color: var(--warn);
  }
  .staging-icon--parachute {
    color: var(--info);
  }
  .staging-icon--clamp {
    color: var(--fg-mute);
  }
  .staging-icon--other {
    color: var(--fg-dim);
  }

  .staging-icon__filled {
    fill: currentColor;
    stroke: currentColor;
  }

  .staging-icon__accent {
    stroke-width: 0.9;
  }

  .staging-icon__accent-light {
    stroke-width: 0.7;
    opacity: 0.6;
  }

  .staging-icon__fallback {
    stroke-dasharray: 1.5 1;
  }

  .staging-icon__fallback-text {
    fill: currentColor;
    stroke: none;
    font-family: var(--font-mono);
    font-size: 10px;
    font-weight: 700;
  }

  /* Interactive wrapper button. Native <button> gives us keyboard
     / focus semantics without the SVG tabindex footgun. Borderless,
     transparent — the child .staging-icon provides all the visible
     chrome. */
  .staging-icon-btn {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    padding: 0;
    margin: 0;
    background: transparent;
    border: none;
    outline: none;
    cursor: context-menu;
    line-height: 0;
    border-radius: 2px;
    transition: filter 150ms ease;
  }
  /* Colour tints on the button mirror the SVG's so currentColor
     references in the SVG resolve via inherit. */
  .staging-icon-btn--engine { color: var(--accent); }
  .staging-icon-btn--decoupler { color: var(--warn); }
  .staging-icon-btn--parachute { color: var(--info); }
  .staging-icon-btn--clamp { color: var(--fg-mute); }
  .staging-icon-btn--other { color: var(--fg-dim); }

  .staging-icon-btn:hover,
  .staging-icon-btn:focus-visible {
    filter: drop-shadow(0 0 3px currentColor);
  }
  .staging-icon-btn:focus-visible {
    outline: 1px solid currentColor;
    outline-offset: 1px;
  }

  /* Symmetry multiplicity badge. Positioned in the bottom-right of
     the button so it reads next to the glyph without overlapping
     it. Uses currentColor so the kind-keyed tint (engine-teal,
     decoupler-amber, etc.) carries through without extra rules. */
  .staging-icon-btn {
    position: relative;
  }
  .staging-icon-btn__count {
    position: absolute;
    right: -4px;
    bottom: -4px;
    font-family: var(--font-mono);
    font-size: 7px;
    font-weight: 600;
    letter-spacing: 0.02em;
    line-height: 1;
    color: currentColor;
    background: var(--bg-panel-solid);
    padding: 1px 2px;
    border-radius: 2px;
    /* Thin outline in the same tint so the badge reads as part of
       the icon and doesn't bleed into neighbouring glyphs in the
       strip. */
    box-shadow: 0 0 0 1px currentColor;
    pointer-events: none;
  }
</style>
