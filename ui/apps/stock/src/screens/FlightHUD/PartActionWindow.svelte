<script lang="ts">
  // Part Action Window — a draggable info tile for one part.
  //
  // Anatomy (see PartActionWindow.css for the full vocabulary):
  //
  //   · anchor crosshair — a small phosphor-green reticle pinned to
  //     the part's live screen projection. Pulses subtly so the pilot
  //     can find it among the vessel's overlapping parts.
  //   · leader line — SVG polyline from anchor to the PAW's nearest
  //     corner, drawn in the anchor layer below the panel. Hides when
  //     the anchor is off-screen.
  //   · panel — compact info tile with corner ticks, a single-row
  //     header (part name · drag grip · close), and one row per
  //     resource: label · bar · percent · dual readout · flow arrow.
  //
  // Drag model: the PAW's viewport position is `anchor + offset`. The
  // offset is either a default (a few px right of the anchor) or a
  // pinned value captured on drag. The PAW therefore "floats with"
  // its part through vessel motion without losing the pilot's chosen
  // layout.

  import { onDestroy } from 'svelte';
  import type { PartActionWindow } from '@dragonglass/telemetry/svelte';
  import type { PartResourceData } from '@dragonglass/telemetry/core';

  interface Props {
    paw: PartActionWindow;
    /** Request a close — host removes the PAW + its subscription. */
    onClose: () => void;
    /** Raise to top of the stacking order — host updates z. */
    onRaise: () => void;
    /** Persist the pilot's drag offset. */
    onPin: (pin: { dx: number; dy: number }) => void;
  }

  const { paw, onClose, onRaise, onPin }: Props = $props();

  // Default offset from the anchor point so a just-opened PAW doesn't
  // cover the part it describes. Right-and-slightly-up reads as
  // "the part speaks out of this window" in western reading order.
  const DEFAULT_OFFSET = { dx: 28, dy: -12 };

  const offset = $derived(paw.pin ?? DEFAULT_OFFSET);

  const anchor = $derived(paw.data?.screen ?? null);
  const anchorVisible = $derived(anchor !== null && anchor.visible);

  // Panel position. When the anchor is off-screen we freeze at the
  // last known placement — better than snapping to (0, 0) while the
  // camera pans. `frozen` is updated from an effect (writes inside a
  // `$derived` are forbidden in Svelte 5) and read as the fallback.
  let frozen = $state<{ x: number; y: number } | null>(null);
  const live = $derived(
    anchorVisible && anchor
      ? { x: anchor.x + offset.dx, y: anchor.y + offset.dy }
      : null,
  );
  $effect(() => {
    if (live) frozen = live;
  });
  const panelPos = $derived(
    live ?? frozen ?? { x: window.innerWidth / 2, y: window.innerHeight / 2 },
  );

  // Drag lifecycle. PointerDown on the header attaches document-level
  // pointermove / pointerup listeners that outlive the header's own
  // event handling. Matches StagingStack's stage-drag pattern —
  // `setPointerCapture` was the alternative but it proved unreliable
  // inside CEF's OSR renderer, so the explicit document listeners
  // are used consistently across the HUD.
  let dragging = $state(false);
  let dragStart: { pointerX: number; pointerY: number; pinX: number; pinY: number } | null = null;

  function onHeaderPointerDown(e: PointerEvent): void {
    if (e.button !== 0) return;
    onRaise();
    dragging = true;
    dragStart = {
      pointerX: e.clientX,
      pointerY: e.clientY,
      pinX: offset.dx,
      pinY: offset.dy,
    };
    document.addEventListener('pointermove', onDocumentPointerMove);
    document.addEventListener('pointerup', onDocumentPointerUp);
    document.addEventListener('pointercancel', onDocumentPointerUp);
  }

  function onDocumentPointerMove(e: PointerEvent): void {
    if (!dragging || !dragStart) return;
    const dx = dragStart.pinX + (e.clientX - dragStart.pointerX);
    const dy = dragStart.pinY + (e.clientY - dragStart.pointerY);
    onPin({ dx, dy });
  }

  function onDocumentPointerUp(): void {
    if (!dragging) return;
    dragging = false;
    dragStart = null;
    document.removeEventListener('pointermove', onDocumentPointerMove);
    document.removeEventListener('pointerup', onDocumentPointerUp);
    document.removeEventListener('pointercancel', onDocumentPointerUp);
  }

  // Defensive cleanup if the PAW unmounts mid-drag (e.g. close click
  // lands while a drag is in flight). The document listeners would
  // otherwise keep firing against a stale onPin reference.
  onDestroy(onDocumentPointerUp);

  // Resource-level format helpers. Percent + dual readout both use
  // the same capacity as the denominator, so nothing here depends on
  // vessel-wide roll-ups — each PAW is fully self-contained.

  function pct(r: PartResourceData): number {
    if (r.capacity <= 0) return 0;
    return (r.available / r.capacity) * 100;
  }

  function stateFor(pct: number): 'nominal' | 'warn' | 'alert' {
    if (pct < 5) return 'alert';
    if (pct < 20) return 'warn';
    return 'nominal';
  }

  // Dual-readout formatting. Resources cover a wide dynamic range
  // (MonoPropellant ~10 units vs SolidFuel ~thousands), so scale the
  // fractional digits by magnitude instead of picking a fixed width.
  function fmtVal(v: number): string {
    if (v >= 1000) return v.toFixed(0);
    if (v >= 100) return v.toFixed(1);
    return v.toFixed(2);
  }

  function fmtFlow(flow: number): string {
    const abs = Math.abs(flow);
    if (abs < 0.01) return '0';
    if (abs >= 100) return abs.toFixed(0);
    if (abs >= 10) return abs.toFixed(1);
    return abs.toFixed(2);
  }

  // Rows materialised once per frame so the template can use them
  // with stable keys and pre-computed gauge state.
  const rows = $derived(
    paw.data?.resources.map((r) => {
      const p = pct(r);
      return {
        key: r.name,
        label: r.abbr,
        pct: p,
        state: stateFor(p),
        available: fmtVal(r.available),
        capacity: fmtVal(r.capacity),
        flow: r.flow,
        flowFmt: r.flow !== undefined ? fmtFlow(r.flow) : '',
      };
    }) ?? [],
  );

  // Leader-line endpoints. Shape: a 45° diagonal leaves the anchor
  // toward the panel; depending on whether the horizontal or vertical
  // gap is larger, a horizontal (or vertical) segment then meets the
  // panel at the attach point.
  //
  //   |dy| ≤ |dx|  →  diagonal to seamY, then horizontal to attach.
  //   |dy| >  |dx|  →  diagonal to attach-x column, then vertical to seam.
  //
  // Binding `offsetHeight` rather than `clientHeight` so we include
  // the 1 px top+bottom border — otherwise a bottom-anchored leader
  // would stop a pixel above the rendered edge.
  const PANEL_WIDTH = 224;

  let panelHeight = $state(0);

  const leader = $derived.by(() => {
    if (!anchorVisible || !anchor || panelHeight <= 0) return null;
    const px = panelPos.x;
    const py = panelPos.y;
    const seamY = anchor.y < py + panelHeight / 2 ? py : py + panelHeight;
    const attachX = Math.max(px + 10, Math.min(px + PANEL_WIDTH - 10, anchor.x));
    const dy = seamY - anchor.y;
    const dx = attachX - anchor.x;
    const dirX = Math.sign(dx) || 1;
    const dirY = Math.sign(dy) || 1;
    const absDx = Math.abs(dx);
    const absDy = Math.abs(dy);
    let ex: number;
    let ey: number;
    if (absDy <= absDx) {
      // 45° diagonal covers the full vertical gap; horizontal
      // segment bridges the remainder to the attach column.
      ex = anchor.x + dirX * absDy;
      ey = seamY;
    } else {
      // 45° diagonal covers the full horizontal gap; vertical
      // segment closes to the seam line.
      ex = attachX;
      ey = anchor.y + dirY * absDx;
    }
    return {
      ax: anchor.x,
      ay: anchor.y,
      ex,
      ey,
      mx: attachX,
      my: seamY,
    };
  });
</script>

<!-- Anchor layer: crosshair + leader line share a viewport-wide SVG so
     coordinates read directly in CSS px. Pointer-events off so the
     overlay never steals clicks from the HUD or the panel. -->
{#if anchorVisible && anchor && leader}
  <svg class="paw-anchor" aria-hidden="true" style="z-index: {paw.z - 1}">
    <path
      class="paw-anchor__leader"
      d="M {leader.ax} {leader.ay} L {leader.ex} {leader.ey} L {leader.mx} {leader.my}"
    />
    <g class="paw-anchor__reticle" style="transform: translate({leader.ax}px, {leader.ay}px)">
      <circle class="paw-anchor__ring" r="7" />
      <circle class="paw-anchor__dot" r="1.6" />
      <line class="paw-anchor__tick" x1="-11" y1="0" x2="-8" y2="0" />
      <line class="paw-anchor__tick" x1="8" y1="0" x2="11" y2="0" />
      <line class="paw-anchor__tick" x1="0" y1="-11" x2="0" y2="-8" />
      <line class="paw-anchor__tick" x1="0" y1="8" x2="0" y2="11" />
    </g>
  </svg>
{/if}

<!-- Panel. Positioned absolutely via inline style so we can drive it
     from reactive viewport coords without fighting a stylesheet. -->
<section
  class="paw"
  class:paw--dragging={dragging}
  class:paw--detached={!anchorVisible}
  style="left: {panelPos.x}px; top: {panelPos.y}px; z-index: {paw.z}"
  bind:offsetHeight={panelHeight}
  aria-label={paw.data?.name ?? 'Part'}
>
  <!-- Corner ticks. Purely decorative — the L-bracket silhouette is a
       visual echo of stock mission-control frames. Outside the padded
       interior so they don't eat content space. -->
  <span class="paw__tick paw__tick--tl" aria-hidden="true"></span>
  <span class="paw__tick paw__tick--tr" aria-hidden="true"></span>
  <span class="paw__tick paw__tick--bl" aria-hidden="true"></span>
  <span class="paw__tick paw__tick--br" aria-hidden="true"></span>

  <header
    class="paw__header"
    role="toolbar"
    tabindex="-1"
    onpointerdown={onHeaderPointerDown}
  >
    <h2 class="paw__title">{paw.data?.name ?? '—'}</h2>
    <button
      type="button"
      class="paw__close"
      aria-label="Close part action window"
      onpointerdown={(e) => e.stopPropagation()}
      onclick={onClose}
    >×</button>
  </header>

  {#if rows.length > 0}
    <ul class="paw__res">
      {#each rows as row (row.key)}
        <li class="paw__res-row paw__res-row--{row.state}">
          <span class="paw__res-label">{row.label}</span>
          <div class="paw__res-body">
            <div class="paw__res-bar-line">
              <div class="paw__res-bar" role="presentation">
                <div class="paw__res-bar-fill" style="--pct: {Math.min(100, row.pct)}%"></div>
                <div class="paw__res-bar-scale" aria-hidden="true">
                  <span></span><span></span><span></span>
                </div>
              </div>
              <span class="paw__res-pct">
                {row.pct < 10 ? row.pct.toFixed(1) : Math.round(row.pct)}<em>%</em>
              </span>
            </div>
            <div class="paw__res-readout-line">
              <span class="paw__res-readout">
                <span class="paw__res-val">{row.available}</span>
                <span class="paw__res-div">/</span>
                <span class="paw__res-cap">{row.capacity}</span>
              </span>
              {#if row.flow !== undefined && Math.abs(row.flow) >= 0.01}
                <span class="paw__res-flow" class:paw__res-flow--neg={row.flow < 0}>
                  <span class="paw__res-flow-arrow" aria-hidden="true">
                    {row.flow > 0 ? '▲' : '▼'}
                  </span>
                  <span class="paw__res-flow-val">{row.flowFmt}</span>
                  <span class="paw__res-flow-unit">/s</span>
                </span>
              {:else}
                <span class="paw__res-flow paw__res-flow--idle" aria-hidden="true">—</span>
              {/if}
            </div>
          </div>
        </li>
      {/each}
    </ul>
  {:else}
    <p class="paw__empty">NO RESOURCES</p>
  {/if}
</section>

<style>
  /* =========================================================
     Part Action Window — detached info tile
     ========================================================= */

  .paw {
    position: fixed;
    width: 224px;
    padding: 0 10px 9px;
    background: var(--bg-panel-strong);
    border: 1px solid var(--line-accent);
    color: var(--fg);
    font-family: var(--font-mono);
    font-size: 11px;
    letter-spacing: 0.02em;
    /* Crisp 1 px frame with no outer glow. Earlier revisions layered
       a 22-px green halo and a 30-px dark drop-shadow behind the
       panel — on a standalone floating tile that read as a wide
       translucent border instead of an edge, so both are gone. The
       inset faint-green rim remains as a subtle phosphor flare. */
    box-shadow: inset 0 0 0 1px rgba(126, 245, 184, 0.05);
    backdrop-filter: blur(14px) saturate(125%);
    -webkit-backdrop-filter: blur(14px) saturate(125%);
    animation: pawIn 0.32s cubic-bezier(0.2, 0.8, 0.25, 1) backwards;
    user-select: none;
    pointer-events: auto;
  }

  .paw--detached {
    border-color: var(--line-bright);
    box-shadow: inset 0 0 0 1px rgba(90, 176, 255, 0.04);
  }

  @keyframes pawIn {
    from {
      opacity: 0;
      transform: scale(0.94);
      filter: blur(3px);
    }
    to {
      opacity: 1;
      transform: scale(1);
      filter: blur(0);
    }
  }

  /* Corner ticks — 6-px L brackets just outside the panel border.
     They read as a thin set of crop marks holding the tile inside a
     notional frame, echoing the curved-tape mission-spec language
     elsewhere on the HUD. */
  .paw__tick {
    position: absolute;
    width: 6px;
    height: 6px;
    pointer-events: none;
  }
  .paw__tick::before,
  .paw__tick::after {
    content: '';
    position: absolute;
    background: var(--accent);
    box-shadow: 0 0 4px var(--accent-glow);
    opacity: 0.75;
  }
  .paw__tick::before { inset: 0 0 auto 0; height: 1px; }
  .paw__tick::after  { inset: 0 auto 0 0; width: 1px; }
  .paw__tick--tl { top: -2px; left: -2px; }
  .paw__tick--tr { top: -2px; right: -2px; transform: scaleX(-1); }
  .paw__tick--bl { bottom: -2px; left: -2px; transform: scaleY(-1); }
  .paw__tick--br { bottom: -2px; right: -2px; transform: scale(-1, -1); }
  .paw--detached .paw__tick::before,
  .paw--detached .paw__tick::after {
    background: var(--info);
    box-shadow: 0 0 4px var(--info-glow);
  }

  /* ---------------------------------------------------------
     Header — kind label, id, drag grip, close
     --------------------------------------------------------- */

  /* Header is now the titlebar. Min-height instead of fixed height so
     long part names can wrap to two lines without chopping glyph
     descenders. The whole bar is the drag handle. */
  .paw__header {
    position: relative;
    display: flex;
    align-items: center;
    gap: 8px;
    min-height: 22px;
    margin: 0 -10px;
    padding: 3px 10px;
    border-bottom: 1px solid var(--line);
    cursor: grab;
    touch-action: none;
  }

  .paw--dragging .paw__header {
    cursor: grabbing;
  }

  .paw__close {
    width: 18px;
    height: 18px;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    font-family: var(--font-mono);
    font-size: 15px;
    line-height: 1;
    color: var(--fg-dim);
    transition: color 160ms ease, background 160ms ease;
    /* Cancel the `grab` cursor inherited from the header so the close
       button reads as a clickable target rather than a drag source. */
    cursor: pointer;
  }
  .paw__close:hover {
    color: var(--alert);
    background: rgba(255, 82, 82, 0.1);
  }

  /* ---------------------------------------------------------
     Title (in the header)
     --------------------------------------------------------- */

  .paw__title {
    flex: 1 1 auto;
    min-width: 0;
    margin: 0;
    font-family: var(--font-display);
    font-size: 13px;
    font-weight: normal;
    letter-spacing: 0.08em;
    text-transform: uppercase;
    color: var(--accent);
    text-shadow: 0 0 6px var(--accent-glow);
    line-height: 1.15;
    /* Two-line clamp so long KSP names ("Rockomax X200-32 Fuel Tank")
       wrap into the titlebar instead of pushing the close button
       off-panel. Beyond two lines, the tail is clipped — names at
       that length are already unusually long. */
    display: -webkit-box;
    -webkit-line-clamp: 2;
    line-clamp: 2;
    -webkit-box-orient: vertical;
    overflow: hidden;
  }

  /* ---------------------------------------------------------
     Resource rows — the payload
     --------------------------------------------------------- */

  .paw__res {
    list-style: none;
    margin: 8px 0 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 6px;
  }

  .paw__empty + .paw__res,
  .paw__empty {
    margin-top: 8px;
  }

  /* Nested flex rather than a single 2-row grid: the bar line and
     the readout line are layout-independent, so a wide flow readout
     on row 2 can't shrink the bar on row 1. Label spans both lines
     as a single flex child; everything else stacks inside the body. */
  .paw__res-row {
    display: flex;
    align-items: stretch;
    gap: 7px;
    /* Per-gauge accent driven by state — mirrors the Propulsion
       convention so LF/OX below threshold here reads with the same
       severity colour it would in the left-stack fuel bars. */
    --gauge-color: var(--accent);
    --gauge-glow: var(--accent-glow);
    --gauge-readout: var(--fg);
  }
  .paw__res-row--warn {
    --gauge-color: var(--warn);
    --gauge-glow: var(--warn-glow);
    --gauge-readout: var(--warn);
  }
  .paw__res-row--alert {
    --gauge-color: var(--alert);
    --gauge-glow: rgba(255, 82, 82, 0.5);
    --gauge-readout: var(--alert);
  }

  .paw__res-label {
    flex: 0 0 22px;
    font-family: var(--font-display);
    font-size: 12px;
    letter-spacing: 0.14em;
    color: var(--fg-dim);
    text-align: center;
    padding: 2px 0;
    border: 1px solid var(--line);
    /* Fill the row so the label reads as a channel tag rather than a
       floating abbreviation. */
    align-self: stretch;
    display: flex;
    align-items: center;
    justify-content: center;
  }
  .paw__res-row--warn  .paw__res-label,
  .paw__res-row--alert .paw__res-label {
    color: var(--gauge-readout);
    border-color: var(--gauge-color);
  }

  /* Body = bar line stacked above readout line. `min-width: 0` so the
     flex child is free to shrink below its intrinsic content width
     (otherwise the child's ideal width would push against the panel
     frame and bleed past the 224-px fixed width). */
  .paw__res-body {
    flex: 1 1 auto;
    min-width: 0;
    display: flex;
    flex-direction: column;
    justify-content: center;
    gap: 2px;
  }

  .paw__res-bar-line {
    display: flex;
    align-items: center;
    gap: 7px;
  }

  .paw__res-readout-line {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 7px;
  }

  .paw__res-bar {
    flex: 1 1 auto;
    min-width: 0;
    position: relative;
    height: 6px;
    background: rgba(46, 106, 85, 0.18);
    border: 1px solid var(--line);
    overflow: visible;
  }

  .paw__res-bar-fill {
    position: absolute;
    inset: 0 auto 0 0;
    width: var(--pct, 0%);
    background: var(--gauge-color);
    box-shadow: 0 0 6px var(--gauge-glow);
    transition:
      width 220ms ease,
      background 260ms ease,
      box-shadow 260ms ease;
  }

  /* Quarter-scale ticks on the bar — four cells of reference depth so
     the pilot's eye has a positional sense of the reading beyond just
     the fill fraction. Absolute-positioned children so they don't
     consume layout width. */
  .paw__res-bar-scale {
    position: absolute;
    inset: 0;
    pointer-events: none;
  }
  .paw__res-bar-scale > span {
    position: absolute;
    top: -2px;
    bottom: -2px;
    width: 1px;
    background: var(--line-bright);
    opacity: 0.6;
  }
  .paw__res-bar-scale > span:nth-child(1) { left: 25%; }
  .paw__res-bar-scale > span:nth-child(2) { left: 50%; }
  .paw__res-bar-scale > span:nth-child(3) { left: 75%; }

  /* Fixed 34 px so the bar's trailing edge lines up across every
     resource row regardless of whether the pct reads "100%" or
     "5.2%". Tabular-nums means the number block's width varies only
     with digit count, and the reserved width is wide enough to hold
     both "100%" and "5.2%" without ragged-right. */
  .paw__res-pct {
    flex: 0 0 34px;
    text-align: right;
    font-family: var(--font-display);
    font-size: 12px;
    color: var(--gauge-readout);
    font-variant-numeric: tabular-nums;
    letter-spacing: 0.02em;
    text-shadow: 0 0 6px var(--gauge-glow);
    transition: color 260ms ease, text-shadow 260ms ease;
    line-height: 1;
  }
  .paw__res-pct em {
    font-family: var(--font-mono);
    font-style: normal;
    font-size: 8px;
    color: var(--fg-mute);
    letter-spacing: 0.12em;
    margin-left: 2px;
    text-shadow: none;
  }

  .paw__res-readout {
    display: inline-flex;
    align-items: baseline;
    gap: 3px;
    font-size: 9px;
    font-variant-numeric: tabular-nums;
    color: var(--fg-dim);
    letter-spacing: 0.04em;
  }
  .paw__res-val {
    color: var(--fg);
  }
  .paw__res-div {
    color: var(--fg-mute);
  }
  .paw__res-cap {
    color: var(--fg-mute);
  }

  .paw__res-flow {
    display: inline-flex;
    align-items: baseline;
    gap: 2px;
    font-family: var(--font-mono);
    font-size: 9px;
    font-variant-numeric: tabular-nums;
    color: var(--accent);
    letter-spacing: 0.04em;
    /* The readout line uses `justify-content: space-between`, so
       flow already rides the right edge — no `justify-self` needed. */
  }
  .paw__res-flow--neg {
    color: var(--warn);
  }
  .paw__res-flow--idle {
    color: var(--fg-mute);
    opacity: 0.55;
  }
  .paw__res-flow-arrow {
    font-size: 7px;
    line-height: 1;
    opacity: 0.9;
    transform: translateY(-1px);
  }
  .paw__res-flow-unit {
    color: var(--fg-mute);
    font-size: 7px;
    letter-spacing: 0.12em;
    margin-left: 1px;
  }

  .paw__empty {
    margin: 6px 0 2px;
    font-size: 9px;
    letter-spacing: 0.24em;
    color: var(--fg-mute);
    text-align: center;
  }

  /* ---------------------------------------------------------
     Anchor layer — leader line + crosshair
     --------------------------------------------------------- */

  :global(.paw-anchor) {
    position: fixed;
    inset: 0;
    width: 100vw;
    height: 100vh;
    pointer-events: none;
    overflow: visible;
  }
  :global(.paw-anchor__leader) {
    fill: none;
    stroke: var(--accent);
    stroke-width: 1;
    stroke-dasharray: 3 3;
    opacity: 0.55;
    filter: drop-shadow(0 0 4px var(--accent-glow));
    animation: leaderPulse 2.6s ease-in-out infinite;
  }
  @keyframes leaderPulse {
    0%, 100% { opacity: 0.45; }
    50%      { opacity: 0.75; }
  }

  :global(.paw-anchor__ring) {
    fill: transparent;
    stroke: var(--accent);
    stroke-width: 1;
    filter: drop-shadow(0 0 4px var(--accent-glow));
  }
  :global(.paw-anchor__dot) {
    fill: var(--accent);
    filter: drop-shadow(0 0 3px var(--accent-glow));
  }
  :global(.paw-anchor__tick) {
    stroke: var(--accent);
    stroke-width: 1;
    opacity: 0.65;
  }
  :global(.paw-anchor__reticle) {
    animation: reticlePulse 2s ease-in-out infinite;
    transform-box: fill-box;
  }
  @keyframes reticlePulse {
    0%, 100% { opacity: 0.75; }
    50%      { opacity: 1; }
  }
</style>
