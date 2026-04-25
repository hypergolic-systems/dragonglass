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

  import { useStageData, useStageOps, useRevertSignal } from '@dragonglass/telemetry/svelte';
  import type { StageEntry } from '@dragonglass/telemetry/core';
  import StageCard from './StageCard.svelte';
  import ContextMenu from './ContextMenu.svelte';
  import type { MenuItem } from './context-menu';
  import { buildPartMenu, type PartRenderItem } from './staging-actions';

  const s = useStageData();
  const ops = useStageOps();
  const revert = useRevertSignal();

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

  // Drop ungrouping state on revert. After a revert-to-launch the
  // ship resets to its assembled configuration — the toggles for
  // "show this group as cousins" reflect a UI choice the pilot made
  // about a now-defunct run, not a stable part-id-keyed preference.
  $effect(() => {
    void revert.revertCount;
    ungrouped = new Set();
  });

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

  // Hover-to-highlight. Enter delivers the full set the hovered
  // icon represents (one for a singleton / expanded cousin, many
  // for a consolidated ×N group); leave sends an empty set which
  // the server treats as "clear all highlights". Latest call wins.
  function onPartHover(persistentIds: readonly string[] | null): void {
    ops.setHighlightParts(persistentIds ?? []);
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
        startX: number;
        startY: number;
        currentY: number;
        // Client-px bounds captured at drag start — used to clamp
        // the card's live translateY so it never leaves the visible
        // stack.
        cardInitialTop: number;
        cardInitialBottom: number;
        stackTop: number;
        stackBottom: number;
        // Pre-drag snapshot of every card's client-px rect. Drop-
        // target computation reads from THIS, not from live
        // `getBoundingClientRect()`, because siblings transform
        // during the drag — a live read would see the shifted
        // positions and oscillate: target → shift → cursor-now-in-
        // hole → target-null → shift-back → target → … .
        cardSnapshot: Array<{ stageNum: number; top: number; bottom: number }>;
        active: boolean;
        target: { insertPos: number } | null;
      };

  const DRAG_THRESHOLD_PX = 5;
  // `.staging-stack-inner` applies `zoom: 0.67` so authored pixels
  // render as 0.67× visible. When we translate the dragged card,
  // the transform lives inside that zoom context too, so we scale
  // the client-px cursor delta up by 1/ZOOM to get authored px.
  const ZOOM_FACTOR = 0.67;
  // Flex gap between cards, in authored px — must stay in lockstep
  // with `.staging-stack-inner { gap: 6px }` below.
  const STACK_GAP_PX = 6;

  let drag = $state<DragState | null>(null);

  // True for a single frame straddling the drop. The sibling-shift
  // transforms were animating via CSS `transition: transform …`;
  // when we clear them on drop, that same transition would play
  // each card sliding back to translate(0). Since the server's
  // echoed state assigns the new content to the SAME keyed DOM
  // node (we key by `stageNum`, which is a slot), the correct
  // visual is: cards stay put, content updates in place. We
  // achieve that by flagging `settling` to disable the transform
  // transition through the tick that clears transforms + applies
  // the new content, then re-enabling it for subsequent drags.
  let settling = $state(false);

  // Per-card drop-hint computation. Part-drag lights up the card
  // the cursor is on. Stage-drag has no per-card hint — the
  // shifted siblings already leave a visible hole at the insertion
  // point, which reads more clearly than a thin accent line.
  function dropHintFor(stage: StageEntry): 'on' | null {
    if (!drag || drag.kind !== 'part' || !drag.active || !drag.target) return null;
    return drag.target.stageNum === stage.stageNum ? 'on' : null;
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
    const cardEl = container?.querySelector<HTMLElement>(
      `[data-stage-num="${stage.stageNum}"]`,
    );
    const cardRect = cardEl?.getBoundingClientRect();
    const stackRect = container?.getBoundingClientRect();
    const allCards = container
      ? Array.from(container.querySelectorAll<HTMLElement>('[data-stage-num]'))
      : [];
    const cardSnapshot = allCards.map((el) => {
      const r = el.getBoundingClientRect();
      return {
        stageNum: Number(el.dataset.stageNum),
        top: r.top,
        bottom: r.bottom,
      };
    });
    drag = {
      kind: 'stage',
      stage,
      startX: e.clientX,
      startY: e.clientY,
      currentY: e.clientY,
      cardInitialTop: cardRect?.top ?? 0,
      cardInitialBottom: cardRect?.bottom ?? 0,
      stackTop: stackRect?.top ?? 0,
      stackBottom: stackRect?.bottom ?? 0,
      cardSnapshot,
      active: false,
      target: null,
    };
    beginDragListeners();
  }

  // Authored-pixel translateY for the currently-dragged stage card,
  // clamped so the card's top / bottom stays inside the stack's
  // visible bounds.
  function stageDragTranslate(d: DragState & { kind: 'stage' }): number {
    const delta = d.currentY - d.startY;
    const minDelta = d.stackTop - d.cardInitialTop;
    const maxDelta = d.stackBottom - d.cardInitialBottom;
    const clamped = Math.max(minDelta, Math.min(maxDelta, delta));
    return clamped / ZOOM_FACTOR;
  }

  // Sibling shift amount during a stage-drag, in authored px. Cards
  // in the affected range slide one "F-card slot" (F's height + a
  // gap) in the direction opposite to the drag, so the stack reads
  // as if F had already been pulled out and the drop slot is a
  // visible hole rather than an abstract line.
  //
  //   I > F (F moving later): cards with F < N < I shift UP.
  //   I < F (F moving earlier): cards with I <= N < F shift DOWN.
  //   Dragged card itself (N === F): 0 — cursor-follow transform
  //                                  is applied separately.
  //   Anything else: 0.
  //
  // We use F's bounding height (the height of the space the drag
  // card actually occupies) so the shift perfectly fills its
  // vacated slot regardless of how many icons F's row is tall.
  function siblingShift(stage: StageEntry): number {
    if (!drag || drag.kind !== 'stage' || !drag.active || !drag.target) return 0;
    const N = stage.stageNum;
    const F = drag.stage.stageNum;
    if (N === F) return 0;
    const I = drag.target.insertPos;
    // F's slot height in authored px: cardInitialBottom - top is in
    // client px, and transforms inside the 0.67× zoom container
    // scale to client, so we convert back to authored.
    const slot =
      (drag.cardInitialBottom - drag.cardInitialTop) / ZOOM_FACTOR
      + STACK_GAP_PX;
    if (I > F && N > F && N < I) return -slot;
    if (I < F && N >= I && N < F) return slot;
    return 0;
  }

  // One number per card: the translate to apply to it right now.
  // For the dragged card this is the clamped cursor delta; for its
  // peers it's the sibling shift that opens a hole where F will
  // drop. Returns `null` when no transform is needed so we don't
  // emit an inline style on every card.
  function cardTranslate(stage: StageEntry): number | null {
    if (!drag || drag.kind !== 'stage' || !drag.active) return null;
    if (drag.stage.stageNum === stage.stageNum) return stageDragTranslate(drag);
    const s = siblingShift(stage);
    return s === 0 ? null : s;
  }

  function beginDragListeners(): void {
    document.addEventListener('pointermove', onDragMove);
    document.addEventListener('pointerup', onDragEnd);
    document.addEventListener('pointercancel', onDragEnd);
  }

  function onDragMove(e: PointerEvent): void {
    if (!drag) return;
    if (drag.kind === 'part') {
      drag.ghostX = e.clientX;
      drag.ghostY = e.clientY;
    } else {
      drag.currentY = e.clientY;
    }
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
        drag.target = computeStageDropTarget(drag, e.clientX, e.clientY);
      }
    }
  }

  function onDragEnd(_e: PointerEvent): void {
    document.removeEventListener('pointermove', onDragMove);
    document.removeEventListener('pointerup', onDragEnd);
    document.removeEventListener('pointercancel', onDragEnd);
    const wasActiveStageDrag =
      drag !== null && drag.active && drag.kind === 'stage';
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
      } else if (isRealMove(drag.target.insertPos, drag.stage.stageNum)) {
        ops.moveStage(drag.stage.stageNum, drag.target.insertPos);
      }
    }
    if (wasActiveStageDrag) {
      // Suppress the transform transition for the frame in which
      // translateY flips to null. Without this, each card animates
      // from its drag-time offset back to 0, visually sliding
      // around while the content updates — the user sees a
      // reshuffle instead of a clean in-place content swap.
      settling = true;
      drag = null;
      // Two rAFs: the first paints with transitions disabled and
      // transforms cleared; the second re-enables transitions so
      // the next drag animates smoothly again.
      requestAnimationFrame(() => {
        requestAnimationFrame(() => {
          settling = false;
        });
      });
    } else {
      drag = null;
    }
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
  // position relative to the cards.
  //
  // Reads from `drag.cardSnapshot` (captured at drag start) instead
  // of live `getBoundingClientRect` — siblings transform as the
  // drop target changes, so a live read oscillates: target → shift
  // → cursor-now-in-hole → target-null → shift-back → repeat. The
  // snapshot fixes this by pinning detection to the pre-drag
  // layout.
  //
  // Always returns some insertPos while the cursor is within the
  // stack's bounds, even for no-op cases (insertPos == F or F+1).
  // Continuity matters: we want the sibling-shift hole to glide
  // smoothly into place as the cursor crosses a neighbour's
  // midpoint, not pop in from nowhere. The real "don't dispatch a
  // no-op move" check lives in `onDragEnd` via `isRealMove`.
  function computeStageDropTarget(
    d: DragState & { kind: 'stage' },
    x: number,
    y: number,
  ): { insertPos: number } | null {
    if (!container) return null;
    const cr = container.getBoundingClientRect();
    if (x < cr.left || x > cr.right) return null;

    const fromStageNum = d.stage.stageNum;
    const others = d.cardSnapshot.filter((c) => c.stageNum !== fromStageNum);
    if (others.length === 0) return null;

    for (const c of others) {
      if (y < c.top || y > c.bottom) continue;
      const mid = (c.top + c.bottom) / 2;
      return { insertPos: y < mid ? c.stageNum : c.stageNum + 1 };
    }
    if (y < others[0].top) {
      return { insertPos: others[0].stageNum };
    }
    if (y > others[others.length - 1].bottom) {
      return { insertPos: others[others.length - 1].stageNum + 1 };
    }
    return null;
  }

  // A drop is a real move only when insertPos lands outside the
  // F / F+1 dead-zone.
  function isRealMove(insertPos: number, fromStageNum: number): boolean {
    return insertPos !== fromStageNum && insertPos !== fromStageNum + 1;
  }
</script>

{#if ordered.length > 0}
  <div class="staging-stack-inner" bind:this={container}>
    {#each ordered as stage (stage.stageNum)}
      <StageCard
        stage={stage}
        active={stage.stageNum === s.currentStageIdx}
        dropHint={dropHintFor(stage)}
        translateY={cardTranslate(stage)}
        isDragging={drag?.kind === 'stage'
          && drag.active
          && drag.stage.stageNum === stage.stageNum}
        {settling}
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

{#if drag && drag.active && drag.kind === 'part'}
  <!-- Part-drag ghost: a small pill pinned to the cursor showing
       the dragged part's icon name (and ×N for symmetry groups).
       Offset via transform so the cursor sits just above the
       chip's top-left corner for a natural "grabbed" feel.
       Stage-drags don't use a ghost — the actual card follows the
       cursor instead, via translateY on the card itself. -->
  <div
    class="drag-ghost drag-ghost--{drag.item.part.kind}"
    style:left="{drag.ghostX}px"
    style:top="{drag.ghostY}px"
  >
    <div class="drag-ghost__chip">
      {drag.item.part.iconName}{drag.item.count > 1 ? ` ×${drag.item.count}` : ''}
    </div>
  </div>
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

  .drag-ghost__chip {
    text-transform: uppercase;
    line-height: 1;
    white-space: nowrap;
  }
</style>
