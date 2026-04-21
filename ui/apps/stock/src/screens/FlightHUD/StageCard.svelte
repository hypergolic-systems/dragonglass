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
    active,
    dropHint,
    ungrouped,
    onPartContext,
    onPartHover,
    onPartDragStart,
  }: {
    stage: StageEntry;
    active: boolean;
    /** Drag-target feedback. `'on'` = a drag will drop onto this
     *  card (move-to-existing-stage). `null` = not a drop target.
     *  New-stage creation is right-click-only — drag cannot create
     *  stages — so there is no insert-above / insert-below variant. */
    dropHint?: 'on' | null;
    /** Representative persistentIds the user has toggled "Ungroup"
     *  on. Passed through so `expandStageParts` can decide whether
     *  to render a consolidated ×N icon or N individual cousins. */
    ungrouped: ReadonlySet<string>;
    /** Right-click / ContextMenu-key on a single part icon. */
    onPartContext?: (item: PartRenderItem, stage: StageEntry, e: MouseEvent | KeyboardEvent) => void;
    /** Hover pass-through. Fires with the hovered part's
     *  persistentId on enter / focus, null on leave / blur. */
    onPartHover?: (persistentId: string | null) => void;
    /** Left-button pointerdown on a part icon. Lets the parent
     *  spin up a drag gesture. */
    onPartDragStart?: (item: PartRenderItem, e: PointerEvent) => void;
  } = $props();

  const dv = $derived(formatDeltaV(stage.deltaVActual));
  const twr = $derived(formatTwr(stage.twrActual));
  const renderItems = $derived(expandStageParts(stage.parts, ungrouped));
</script>

<section
  class="stage-card"
  class:stage-card--active={active}
  class:stage-card--drop-on={dropHint === 'on'}
  data-stage-num={stage.stageNum}
  aria-label={`Stage ${stage.stageNum}`}
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
          onhover={onPartHover}
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
    transition:
      background 220ms ease,
      border-color 220ms ease,
      box-shadow 220ms ease;
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
</style>
