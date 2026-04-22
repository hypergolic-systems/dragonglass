<script lang="ts">
  // Staging stack — vertical list of StageCards, one per operating
  // stage of the active vessel. Renders inside the bottom-left
  // `.staging-stack` flex column (`flex-direction: column-reverse`),
  // above the Propulsion panel.
  //
  // DOM ordering: stages sorted ascending by `stageNum`. The inner
  // container uses plain `column` flex, so DOM-first renders at the
  // top — lowest stageNum (final / payload stage) on top, highest
  // stageNum (current / next-to-fire booster) at the bottom. The
  // outer `.staging-stack` then stacks Propulsion below us via its
  // own column-reverse.
  //
  // Input capture. The wrapper paints a near-invisible `alpha: 0.01`
  // background so every pixel in the stack's bounds — including the
  // gaps between cards and the scrollbar gutter — passes the
  // plugin's `(alpha > 0)` hit test in DragonglassHudAddon's
  // SampleAndForwardMouse path. Without it, wheel events over the
  // inter-card gaps would pass through to KSP (zoom-camera), not
  // scroll our list.
  //
  // Scrolling. `max-height` caps the visible stack to ~half the
  // viewport so tall vessels don't overflow into KSP's clipped
  // region. The calc undoes the 0.67 zoom applied to this container,
  // since children (including max-height's effect on overflow) are
  // layed out pre-zoom. Scroll happens natively once the wrapper
  // holds focus or the wheel fires over it — INPUT_MOUSE_WHEEL
  // already forwards to CEF from the plugin's input ring.
  //
  // Auto-scroll to active. After each frame where the active stage
  // changes, bring the active card into view via `scrollIntoView`
  // with `block: 'nearest'` — no-op when already visible, minimal
  // scroll otherwise. Doesn't fight a user scrolling up to inspect
  // an earlier stage unless they press space and trigger a new
  // active stage.

  import { useStageData, useStageOps } from '@dragonglass/telemetry/svelte';
  import type { StageEntry } from '@dragonglass/telemetry/core';
  import StageCard from './StageCard.svelte';
  import ContextMenu, { type MenuItem } from './ContextMenu.svelte';
  import { buildPartMenu, type PartRenderItem } from './staging-actions';

  const s = useStageData();
  const ops = useStageOps();

  const ordered = $derived(
    [...s.stages].sort((a, b) => a.stageNum - b.stageNum),
  );

  let container = $state<HTMLElement | null>(null);

  $effect(() => {
    // Depend on the current stage id so the effect fires on staging
    // transitions. The DOM query runs after Svelte has updated the
    // .stage-card--active class on the freshly-active card.
    void s.currentStageIdx;
    if (!container) return;
    const active = container.querySelector<HTMLElement>('.stage-card--active');
    if (active) active.scrollIntoView({ block: 'nearest' });
  });

  // ---- Right-click context menu state ----
  //
  // A single menu lives at the StagingStack level so at most one is
  // open at a time. Menu items close over the ops handle so each
  // action is a one-liner at the call site.
  let menu = $state<{ items: MenuItem[]; x: number; y: number } | null>(null);

  function openMenu(items: MenuItem[], e: MouseEvent | KeyboardEvent): void {
    // For MouseEvent we have clientX/Y; for KeyboardEvent we place
    // the menu near the focused element's bounding rect.
    let x = 0;
    let y = 0;
    if (e instanceof MouseEvent) {
      x = e.clientX;
      y = e.clientY;
    } else {
      const target = e.currentTarget as Element | null;
      if (target) {
        const r = target.getBoundingClientRect();
        x = r.right;
        y = r.bottom;
      }
    }
    menu = { items, x, y };
  }

  // Client-only "ungroup" state. Holds representative persistentIds
  // whose symmetry group the user has asked to see individually. The
  // server never learns about this — it's purely a rendering toggle
  // that expands one consolidated "×N" icon into N cousins. Moves
  // dispatched from individual-cousin icons carry `group: false` so
  // the server treats them as per-part operations.
  let ungrouped = $state<Set<string>>(new Set());

  function toggleUngroup(repId: string): void {
    const next = new Set(ungrouped);
    if (next.has(repId)) next.delete(repId);
    else next.add(repId);
    ungrouped = next;
  }

  function onPartContext(
    item: PartRenderItem,
    stage: StageEntry,
    e: MouseEvent | KeyboardEvent,
  ): void {
    const items = buildPartMenu(
      item,
      stage.stageNum,
      s.stages,
      ops,
      toggleUngroup,
      (id) => ungrouped.has(id),
    );
    openMenu(items, e);
  }

  // Hover-to-highlight. Fires `setHighlightPart` on enter/leave of
  // any icon. The server tracks the single currently-highlighted
  // part and swaps when a new id arrives — so moving between icons
  // glows one at a time and leaving the stack clears it.
  function onPartHover(persistentId: string | null): void {
    ops.setHighlightPart(persistentId);
  }

  // ---- Drag-and-drop -----------------------------------------
  //
  // Two gesture flavours share the same pointerdown threshold +
  // document-level move/up tracking:
  //
  //   'part'   — the user pressed on a part icon. Drop target is a
  //              whole stage card; releasing moves the part (or its
  //              symmetry group, depending on the render item's
  //              `isConsolidated` flag) into that stage via movePart.
  //
  //   'stage'  — the user pressed on a stage card's body, outside
  //              any icon. Drop target is an insertion POINT
  //              between cards (top half of a card → above it,
  //              bottom half → below; outside any card but over
  //              the stack's x extent → top/bottom of the stack).
  //              Releasing calls moveStage.
  //
  // Creating new stages is right-click-menu-only for parts —
  // dragging a part never creates stages. Dragging a stage is
  // reordering only; no new stage comes into existence.

  type DragState =
    | {
        kind: 'part';
        item: PartRenderItem;
        ghostX: number;
        ghostY: number;
        startX: number;
        startY: number;
        active: boolean;
        target: { stageNum: number } | null;
      }
    | {
        kind: 'stage';
        stage: StageEntry;
        ghostX: number;
        ghostY: number;
        startX: number;
        startY: number;
        active: boolean;
        target: { insertPos: number } | null;
      };

  const DRAG_THRESHOLD_PX = 5;

  let drag = $state<DragState | null>(null);

  // Per-card drop-hint computation. Part drag lights up the card
  // the cursor is on; stage drag lights up the insertion edge.
  function dropHintFor(stage: StageEntry): 'on' | 'insert-above' | 'insert-below' | null {
    if (!drag || !drag.active || !drag.target) return null;
    if (drag.kind === 'part') {
      return drag.target.stageNum === stage.stageNum ? 'on' : null;
    }
    // Stage drag — render the insertion bar on the top of the card
    // whose stageNum equals insertPos, or on the bottom of the last
    // card when insertPos is past the end.
    const insertPos = drag.target.insertPos;
    if (insertPos === stage.stageNum) return 'insert-above';
    const maxStageNum = ordered.length > 0 ? ordered[ordered.length - 1].stageNum : -1;
    if (insertPos === maxStageNum + 1 && stage.stageNum === maxStageNum) {
      return 'insert-below';
    }
    return null;
  }

  function onPartDragStart(item: PartRenderItem, e: PointerEvent): void {
    drag = {
      kind: 'part',
      item,
      ghostX: e.clientX,
      ghostY: e.clientY,
      startX: e.clientX,
      startY: e.clientY,
      active: false,
      target: null,
    };
    beginDragListeners();
  }

  function onStageDragStart(stage: StageEntry, e: PointerEvent): void {
    drag = {
      kind: 'stage',
      stage,
      ghostX: e.clientX,
      ghostY: e.clientY,
      startX: e.clientX,
      startY: e.clientY,
      active: false,
      target: null,
    };
    beginDragListeners();
  }

  function beginDragListeners(): void {
    document.addEventListener('pointermove', onDragMove);
    document.addEventListener('pointerup', onDragEnd);
    document.addEventListener('pointercancel', onDragEnd);
  }

  function onDragMove(e: PointerEvent): void {
    if (!drag) return;
    drag.ghostX = e.clientX;
    drag.ghostY = e.clientY;
    if (!drag.active) {
      const dx = e.clientX - drag.startX;
      const dy = e.clientY - drag.startY;
      if (dx * dx + dy * dy >= DRAG_THRESHOLD_PX * DRAG_THRESHOLD_PX) {
        drag.active = true;
      }
    }
    if (drag.active) {
      if (drag.kind === 'part') {
        drag.target = computePartDropTarget(e.clientX, e.clientY);
      } else {
        drag.target = computeStageDropTarget(e.clientX, e.clientY, drag.stage.stageNum);
      }
    }
  }

  function onDragEnd(_e: PointerEvent): void {
    document.removeEventListener('pointermove', onDragMove);
    document.removeEventListener('pointerup', onDragEnd);
    document.removeEventListener('pointercancel', onDragEnd);
    if (drag && drag.active && drag.target) {
      if (drag.kind === 'part') {
        // Group flag follows the rendered item: consolidated "×N"
        // icons move the whole symmetry set; cousin icons revealed
        // by "Ungroup" move only themselves.
        ops.movePart(
          drag.item.part.persistentId,
          drag.target.stageNum,
          drag.item.isConsolidated,
        );
      } else {
        ops.moveStage(drag.stage.stageNum, drag.target.insertPos);
      }
    }
    drag = null;
  }

  function computePartDropTarget(x: number, y: number): { stageNum: number } | null {
    if (!container) return null;
    const cards = Array.from(
      container.querySelectorAll<HTMLElement>('[data-stage-num]'),
    );
    for (const el of cards) {
      const r = el.getBoundingClientRect();
      if (x < r.left || x > r.right) continue;
      if (y < r.top || y > r.bottom) continue;
      return { stageNum: Number(el.dataset.stageNum) };
    }
    return null;
  }

  // Stage-drag: figure out the insertion point from the cursor's
  // position relative to the cards. Returns null when the move
  // would be a no-op (insertPos equal to `from` or `from + 1`,
  // which means the stage doesn't actually change places).
  function computeStageDropTarget(
    x: number,
    y: number,
    fromStageNum: number,
  ): { insertPos: number } | null {
    if (!container) return null;
    const cards = Array.from(
      container.querySelectorAll<HTMLElement>('[data-stage-num]'),
    );
    if (cards.length === 0) return null;

    const cr = container.getBoundingClientRect();
    if (x < cr.left || x > cr.right) return null;

    let insertPos: number | null = null;
    for (const el of cards) {
      const r = el.getBoundingClientRect();
      if (y < r.top || y > r.bottom) continue;
      const stageNum = Number(el.dataset.stageNum);
      const mid = (r.top + r.bottom) / 2;
      insertPos = y < mid ? stageNum : stageNum + 1;
      break;
    }
    if (insertPos === null) {
      // Above first or below last.
      const firstRect = cards[0].getBoundingClientRect();
      const lastRect = cards[cards.length - 1].getBoundingClientRect();
      if (y < firstRect.top) {
        insertPos = Number(cards[0].dataset.stageNum);
      } else if (y > lastRect.bottom) {
        insertPos = Number(cards[cards.length - 1].dataset.stageNum) + 1;
      } else {
        return null;
      }
    }
    // No-op cases.
    if (insertPos === fromStageNum || insertPos === fromStageNum + 1) {
      return null;
    }
    return { insertPos };
  }
</script>

{#if ordered.length > 0}
  <div class="staging-stack-inner" bind:this={container}>
    {#each ordered as stage (stage.stageNum)}
      <StageCard
        stage={stage}
        active={stage.stageNum === s.currentStageIdx}
        dropHint={dropHintFor(stage)}
        {ungrouped}
        {onPartContext}
        {onPartHover}
        {onPartDragStart}
        {onStageDragStart}
      />
    {/each}
  </div>
{/if}

{#if menu}
  <ContextMenu
    items={menu.items}
    x={menu.x}
    y={menu.y}
    onDismiss={() => (menu = null)}
  />
{/if}

{#if drag && drag.active}
  <!-- Ghost preview: a small pill pinned to the cursor showing
       what's being dragged. Offset so the cursor sits just above the
       chip's top-left corner for a natural "grabbed" feel. -->
  {#if drag.kind === 'part'}
    <div
      class="drag-ghost drag-ghost--{drag.item.part.kind}"
      style:left="{drag.ghostX}px"
      style:top="{drag.ghostY}px"
    >
      <div class="drag-ghost__chip">
        {drag.item.part.iconName}{drag.item.count > 1 ? ` ×${drag.item.count}` : ''}
      </div>
    </div>
  {:else}
    <div
      class="drag-ghost drag-ghost--stage"
      style:left="{drag.ghostX}px"
      style:top="{drag.ghostY}px"
    >
      <div class="drag-ghost__chip">
        STAGE {String(drag.stage.stageNum).padStart(2, '0')}
      </div>
    </div>
  {/if}
{/if}

<style>
  .staging-stack-inner {
    display: flex;
    flex-direction: column;
    /* 6px authored → ~4 CSS px at the 0.67 zoom below. Tight
       enough that the cards read as a single column but with
       just enough separation that active-card chrome doesn't
       fuse visually into the card above it. */
    gap: 6px;
    width: 120px;
    /* Match Propulsion's proportional shrink so the stack visually
       scales with the panel beneath it. */
    zoom: 0.67;
    /* Near-invisible background so the whole panel hit-tests as
       opaque to the CEF alpha gate (see DragonglassHudAddon
       HitTestAlpha). Alpha ~13/255 — below the threshold of
       visibility but well above the `(bgra >> 24) > 0` hit-test
       floor, so right-click / wheel events over the gaps between
       cards still claim input instead of falling through to KSP's
       camera. Kept higher than the theoretical minimum of 1/255
       because CEF's compositor can round alpha values down during
       IOSurface blits, and we'd rather not leave margin-of-error
       hit-test holes. */
    background: rgba(0, 0, 0, 0.05);
    /* Height cap — 80% of the viewport, divided by the 0.67 zoom
       so the authored constraint renders as 80 vh after the child
       shrink. Plenty of room for tall vessels; the rest scrolls. */
    max-height: calc(80vh / 0.67);
    overflow-y: auto;
    overflow-x: hidden;
    /* Firefox thin-scrollbar syntax. Chromium uses the
       ::-webkit-scrollbar pseudo-elements below — both compile;
       whichever the browser understands takes effect. */
    scrollbar-width: thin;
    scrollbar-color: var(--line-bright) transparent;
    /* Padding-right gives the scrollbar gutter so the cards don't
       reflow when the scrollbar appears/disappears. */
    padding: 2px 3px 2px 2px;
    /* Staggered entry after the navslot + Propulsion animate in
       (0.34s + 0.42s respectively in FlightHUD.css / Propulsion.css).
       We come last so the card stack feels like the final layer
       snapping into place. */
    animation: panelIn 0.8s cubic-bezier(0.2, 0.8, 0.25, 1) backwards;
    animation-delay: 0.5s;
  }

  /* Chromium scrollbar — matches the instrument aesthetic. Thin
     vertical rail with a muted thumb that brightens on hover. */
  .staging-stack-inner::-webkit-scrollbar {
    width: 6px;
  }
  .staging-stack-inner::-webkit-scrollbar-track {
    background: transparent;
  }
  .staging-stack-inner::-webkit-scrollbar-thumb {
    background: var(--line-bright);
    border-radius: 2px;
  }
  .staging-stack-inner::-webkit-scrollbar-thumb:hover {
    background: var(--accent-dim);
  }

  /* Drag ghost — follows the cursor during a drag. Fixed-position
     so the page-scroll transform on the stack container doesn't
     drag us along. Offset via translate so the cursor is at the
     chip's top-left corner rather than dead centre — reads more
     like a grabbed-object affordance. Kind-keyed tints keep it
     consistent with the originating glyph family. */
  .drag-ghost {
    position: fixed;
    z-index: 200;
    pointer-events: none;
    transform: translate(8px, 8px);
    background: var(--bg-panel-solid);
    border: 1px solid var(--line-accent);
    box-shadow:
      0 0 16px rgba(126, 245, 184, 0.25),
      0 4px 14px rgba(0, 0, 0, 0.55);
    padding: 3px 6px;
    font-family: var(--font-mono);
    font-size: 9px;
    letter-spacing: 0.12em;
    color: var(--fg);
    opacity: 0.94;
  }

  .drag-ghost--engine { color: var(--accent); }
  .drag-ghost--decoupler { color: var(--warn); }
  .drag-ghost--parachute { color: var(--info); }
  .drag-ghost--clamp { color: var(--fg-mute); }
  .drag-ghost--other { color: var(--fg-dim); }
  .drag-ghost--stage { color: var(--accent); }

  .drag-ghost__chip {
    text-transform: uppercase;
    line-height: 1;
    white-space: nowrap;
  }
</style>
