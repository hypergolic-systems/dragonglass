<script lang="ts">
  // One card in the staging stack: stage number badge, two compact
  // stat rows (ΔV / TWR), and an icon strip for the parts that
  // activate or separate on this stage.
  //
  // Typography + panel treatment mirrors the Propulsion panel exactly
  // so the stack reads as a single instrument family: same 120-px
  // width, same 7/8 padding, same bg-panel-strong + line-accent chrome
  // on the *active* card, same Unica One readout at 13 px with a teal
  // glow. Inactive / future stages sit one step back on the same
  // axis — quieter panel (bg-panel + subtle line), values rendered
  // in --fg without the glow so they're clearly previews rather than
  // a live telemetry surface.
  //
  // Section rhythm echoes Propulsion's pattern of `border-top: 1px
  // solid var(--line)` between logical groups (stats → icon strip),
  // so the eye sweeps the card with the same cadence it uses on
  // Propulsion below.

  import type { StageEntry } from '@dragonglass/telemetry/core';
  import StagingIcon from './StagingIcon.svelte';
  import { formatDeltaV, formatTwr } from './format';
  import { expandStageParts } from './stage-render';
  import type { PartRenderItem } from './staging-actions';

  let {
    stage,
    cumulativeDeltaV,
    active,
    dropHint,
    translateY,
    isDragging = false,
    settling = false,
    ungrouped,
    onPartContext,
    onPartHover,
    onPartDragStart,
    onStageDragStart,
  }: {
    stage: StageEntry;
    /** Δv (m/s) summed from this stage onward through every stage
     *  that fires after it — i.e. the running total a pilot watching
     *  this stage burn would consume + retain to reach orbit. Top of
     *  the stack (final stage) shows just its own Δv; bottom (first
     *  to fire) shows the whole vessel's Δv budget. Optional — when
     *  absent, the TOTAL row hides. */
    cumulativeDeltaV?: number;
    active: boolean;
    /** Authored-pixel vertical offset during stage-drag. For the
     *  dragged card itself this is the clamped cursor delta; for
     *  sibling cards it's the shift that opens the drop-slot hole.
     *  Null / absent = rest normally in the flex layout. */
    translateY?: number | null;
    /** True when this card is the one the user is currently
     *  dragging. Drives the lift / glow / no-transition chrome —
     *  sibling cards shift via `translateY` but stay flat. */
    isDragging?: boolean;
    /** True for a single frame right around a drop. Disables the
     *  transform transition so cards snap straight to translate(0)
     *  when `translateY` clears, instead of animating back from
     *  their drag-time offsets over the content swap. */
    settling?: boolean;
    /** Drag-target feedback:
     *    'on'  — a part-drag will drop into this stage.
     *    null  — not a drop target.
     *  Stage-drag has no per-card hint; the sibling-shift hole IS
     *  the insertion indicator. */
    dropHint?: 'on' | null;
    /** Representative persistentIds the user has toggled "Ungroup"
     *  on. Passed through so `expandStageParts` can decide whether
     *  to render a consolidated ×N icon or N individual cousins. */
    ungrouped: ReadonlySet<string>;
    /** Right-click / ContextMenu-key on a single part icon. */
    onPartContext?: (item: PartRenderItem, stage: StageEntry, e: MouseEvent | KeyboardEvent) => void;
    /** Hover pass-through. Fires on enter / focus with the set of
     *  persistentIds the icon visually represents — one for a
     *  singleton or expanded-cousin icon, many for a consolidated
     *  ×N group. `null` fires on leave / blur. */
    onPartHover?: (persistentIds: readonly string[] | null) => void;
    /** Left-button pointerdown on a part icon. Lets the parent
     *  spin up a drag gesture. */
    onPartDragStart?: (item: PartRenderItem, e: PointerEvent) => void;
    /** Left-button pointerdown on the card body (outside any
     *  icon). Starts a stage-drag gesture — the whole stage moves
     *  as a unit. */
    onStageDragStart?: (stage: StageEntry, e: PointerEvent) => void;
  } = $props();

  function onCardPointerDown(e: PointerEvent): void {
    if (e.button !== 0 || !onStageDragStart) return;
    // If the press landed on a part icon, let the icon's handler
    // take it — drag-part and drag-stage share the left-button
    // pointerdown gesture but differ in where the cursor is.
    const target = e.target as Element | null;
    if (target?.closest('.staging-icon-btn')) return;
    onStageDragStart(stage, e);
  }

  const dv = $derived(formatDeltaV(stage.deltaVActual));
  const twr = $derived(formatTwr(stage.twrActual));
  const total = $derived(
    cumulativeDeltaV != null ? formatDeltaV(cumulativeDeltaV) : null,
  );
  const renderItems = $derived(expandStageParts(stage.parts, ungrouped));
</script>

<!-- svelte-ignore a11y_no_static_element_interactions -->
<section
  class="stage-card"
  class:stage-card--active={active}
  class:stage-card--drop-on={dropHint === 'on'}
  class:stage-card--dragging={isDragging}
  class:stage-card--settling={settling}
  data-stage-num={stage.stageNum}
  aria-label={`Stage ${stage.stageNum}`}
  onpointerdown={onStageDragStart ? onCardPointerDown : undefined}
  style:transform={translateY != null ? `translateY(${translateY}px)` : undefined}
>
  <header class="stage-card__head">
    <span class="stage-card__head-label">Stage</span>
    <span class="stage-card__num">{String(stage.stageNum).padStart(2, '0')}</span>
  </header>

  <div class="stage-card__stats">
    <div class="stage-card__row">
      <span class="stage-card__label">&#916;V</span>
      <span class="stage-card__value">
        <span class="stage-card__num-text" class:stage-card__num-text--null={dv.value === '—'}>
          {dv.value}
        </span>
      </span>
    </div>
    {#if total}
      <div class="stage-card__row stage-card__row--total">
        <span class="stage-card__label">TOT</span>
        <span class="stage-card__value">
          <span
            class="stage-card__num-text stage-card__num-text--total"
            class:stage-card__num-text--null={total.value === '—'}
          >
            {total.value}
          </span>
        </span>
      </div>
    {/if}
    <div class="stage-card__row">
      <span class="stage-card__label">TWR</span>
      <span class="stage-card__value">
        <span class="stage-card__num-text" class:stage-card__num-text--null={twr === '—'}>
          {twr}
        </span>
      </span>
    </div>
  </div>

  {#if renderItems.length > 0}
    <div class="stage-card__icons">
      {#each renderItems as item (item.part.persistentId)}
        <StagingIcon
          iconName={item.part.iconName}
          kind={item.part.kind}
          persistentId={item.part.persistentId}
          symmetryCount={item.count}
          oncontext={onPartContext ? (e) => onPartContext(item, stage, e) : undefined}
          onhover={onPartHover
            ? (id) => onPartHover(
                id === null
                  ? null
                  // Consolidated group: light up every cousin at
                  // once (what the single visible icon represents).
                  // Expanded or singleton: just the one part.
                  : item.isConsolidated
                    ? [item.part.persistentId, ...item.part.cousinsInStage]
                    : [item.part.persistentId],
              )
            : undefined}
          ondragstart={onPartDragStart ? (e) => onPartDragStart(item, e) : undefined}
        />
      {/each}
    </div>
  {/if}
</section>

<style>
  /* =========================================================
     Card chrome — mirrors Propulsion's .prop for the active
     state; tones down one level for future stages.
     ========================================================= */

  .stage-card {
    width: 120px;
    padding: 6px 8px 7px;
    /* Opaque panel — drops the translucency + backdrop blur so the
       card reads the same against every scene background (the
       transparent treatment behaved inconsistently over bright
       skyboxes). Future stages still sit one step back from the
       active card via a subtler border / dimmer numbers, but now
       on a solid fill. */
    background: var(--bg-panel-solid);
    border: 1px solid var(--line);
    color: var(--fg);
    font-family: var(--font-mono);
    font-size: 11px;
    letter-spacing: 0.02em;
    /* Transition the transform so sibling cards animate smoothly
       into the hole as it moves during a stage-drag. Dragged card
       itself overrides this to 'none' (see .stage-card--dragging)
       so it stays glued to the cursor. */
    transition:
      background 220ms ease,
      border-color 220ms ease,
      box-shadow 220ms ease,
      transform 150ms ease;
  }

  /* Active — full Propulsion treatment minus the blur. Same solid
     fill as future cards (so the whole stack paints cleanly against
     any scene), with the accent border + teal glow + inner shadow
     singling out the live stage. */
  .stage-card--active {
    border-color: var(--line-accent);
    box-shadow:
      0 0 22px rgba(126, 245, 184, 0.06),
      inset 0 0 0 1px rgba(126, 245, 184, 0.04);
  }

  /* =========================================================
     Header — "STAGE  05" with Propulsion's label metrics +
     Unica One for the number.
     ========================================================= */

  .stage-card__head {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 6px;
    padding-bottom: 5px;
    border-bottom: 1px solid var(--line);
  }

  .stage-card__head-label {
    font-family: var(--font-mono);
    font-size: 7px;
    letter-spacing: 0.18em;
    text-transform: uppercase;
    font-weight: 500;
    color: var(--fg-dim);
  }

  .stage-card__num {
    font-family: var(--font-display);
    font-size: 14px;
    line-height: 1;
    color: var(--fg-dim);
    font-variant-numeric: tabular-nums;
    letter-spacing: 0.04em;
    transition: color 280ms ease, text-shadow 280ms ease;
  }

  .stage-card--active .stage-card__num {
    color: var(--accent);
    text-shadow: 0 0 8px var(--accent-glow);
  }

  /* =========================================================
     Stat rows — identical rhythm to Propulsion's .prop__stats
     so the two panels parse as one typographic system.
     ========================================================= */

  .stage-card__stats {
    display: flex;
    flex-direction: column;
    gap: 2px;
    margin-top: 5px;
  }

  .stage-card__row {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 6px;
    padding: 1px 0;
    min-height: 13px;
  }

  .stage-card__label {
    font-family: var(--font-mono);
    font-size: 7px;
    letter-spacing: 0.18em;
    color: var(--fg-dim);
    text-transform: uppercase;
    font-weight: 500;
    flex: 0 0 auto;
  }

  .stage-card__value {
    display: inline-flex;
    align-items: last baseline;
    gap: 3px;
    line-height: 1;
  }

  .stage-card__num-text {
    font-family: var(--font-display);
    font-size: 13px;
    line-height: 1;
    color: var(--fg);
    font-variant-numeric: tabular-nums;
    letter-spacing: 0.01em;
    transition: color 280ms ease, text-shadow 280ms ease;
  }

  /* Active card: accent + glow — matches Propulsion's .prop__num
     treatment exactly. */
  .stage-card--active .stage-card__num-text {
    color: var(--accent);
    text-shadow: 0 0 8px var(--accent-glow);
  }

  .stage-card__num-text--null {
    color: var(--fg-dim);
    text-shadow: none;
  }

  /* Cumulative-Δv row sits between ΔV and TWR — same numeric weight
     as the per-stage ΔV (13 px Unica One) but rendered in --fg
     without the active-card glow, so the eye still finds per-stage
     ΔV first and reads TOT as the running total it sums into. The
     opacity step keeps it tonally subordinate without making the
     numeric itself smaller. */
  .stage-card__row--total {
    opacity: 0.78;
  }
  .stage-card__num-text--total {
    color: var(--fg);
    text-shadow: none;
  }
  .stage-card--active .stage-card__num-text--total {
    color: var(--fg);
    text-shadow: none;
  }

  /* =========================================================
     Icon strip — separated by the same internal divider
     Propulsion uses between its sections.
     ========================================================= */

  .stage-card__icons {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 4px 5px;
    margin-top: 6px;
    padding-top: 6px;
    border-top: 1px solid var(--line);
  }

  /* Future stages: dim the icon strip in sympathy with the muted
     numeric readouts, so the card reads as a preview at a glance.
     The icons' own --accent / --warn / --info tints still come
     through, just attenuated. */
  .stage-card:not(.stage-card--active) .stage-card__icons {
    opacity: 0.62;
    filter: saturate(0.82);
  }

  /* =========================================================
     Drag-drop feedback. `'drop-on'` = the dragged part will land
     in this stage. Dashed teal outline + accent glow so the
     target reads as a distinct affordance from the standard
     active-stage chrome.
     ========================================================= */

  .stage-card--drop-on {
    outline: 1px dashed var(--accent);
    outline-offset: 1px;
    box-shadow: 0 0 16px rgba(126, 245, 184, 0.18);
  }

  /* Insertion-line hints for stage-drag. The bar sits at the card
     edge where the dragged stage would land; uses the accent colour
     so it reads as a first-class drop affordance. */
  .stage-card {
    position: relative;
  }
  .stage-card--insert-above::before,
  .stage-card--insert-below::after {
    content: '';
    position: absolute;
    left: -2px;
    right: -2px;
    height: 2px;
    background: var(--accent);
    box-shadow: 0 0 8px var(--accent-glow);
    pointer-events: none;
  }
  .stage-card--insert-above::before {
    top: -4px;
  }
  .stage-card--insert-below::after {
    bottom: -4px;
  }

  /* Stage-drag cursor cue on the non-icon portions of the card. */
  .stage-card {
    cursor: grab;
  }
  .stage-card:active,
  .stage-card--dragging {
    cursor: grabbing;
  }

  /* Lift the actively-dragged card out of the stack visually:
     stacking context above siblings, a drop shadow that strengthens
     the "picked up" feeling, and explicit `transform` response
     (no transition) so the card stays glued to the cursor. */
  .stage-card--dragging {
    z-index: 20;
    box-shadow:
      0 0 28px rgba(126, 245, 184, 0.18),
      0 12px 28px rgba(0, 0, 0, 0.55),
      inset 0 0 0 1px rgba(126, 245, 184, 0.12);
    transition: none;
  }

  /* Settling: one-frame transform-transition suppression around
     a drop, so cards snap to translate(0) in the same tick that
     their content swaps — instead of animating from their drag-
     time offsets back to the flex layout while the new content
     is already showing. */
  .stage-card--settling {
    transition:
      background 220ms ease,
      border-color 220ms ease,
      box-shadow 220ms ease;
  }
</style>
