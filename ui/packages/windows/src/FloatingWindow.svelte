<script lang="ts">
  // Floating window primitive. Drags from header, resizes from any
  // of 8 edge handles, raises on pointerdown. Visual styling is kept
  // minimal — corners are 1 px, header is a thin grip, body is a
  // bare slot — so consumers can layer their own panel chrome on top
  // (see Nova's VesselPanel for an example). Drag/resize implementation
  // mirrors the PartActionWindow pattern: document-level pointermove
  // listeners attached on pointerdown, removed on pointerup, with
  // explicit cleanup on unmount in case a drag is in flight when the
  // window closes.

  import { onDestroy } from 'svelte';
  import type {
    FloatingWindowPos,
    FloatingWindowSize,
    FloatingWindowProps,
  } from './types';

  const {
    title = '',
    defaultPos = { x: 80, y: 80 },
    defaultSize = { w: 360, h: 320 },
    minSize = { w: 200, h: 120 },
    z = 100,
    onClose,
    onRaise,
    header,
    children,
  }: FloatingWindowProps = $props();

  // svelte-ignore state_referenced_locally — `defaultPos` / `defaultSize`
  // intentionally seed initial values only. Subsequent prop updates from
  // the consumer would not redock an already-positioned window.
  let pos = $state<FloatingWindowPos>({ x: defaultPos.x, y: defaultPos.y });
  // svelte-ignore state_referenced_locally
  let size = $state<FloatingWindowSize>({ w: defaultSize.w, h: defaultSize.h });

  type DragState = {
    pointerX: number;
    pointerY: number;
    startPosX: number;
    startPosY: number;
  };

  type ResizeDir = 'n' | 's' | 'e' | 'w' | 'ne' | 'nw' | 'se' | 'sw';

  type ResizeState = {
    dir: ResizeDir;
    pointerX: number;
    pointerY: number;
    startPosX: number;
    startPosY: number;
    startW: number;
    startH: number;
  };

  let dragging = $state(false);
  let dragStart: DragState | null = null;
  let resizing = $state<ResizeState | null>(null);

  function clampPos(p: FloatingWindowPos): FloatingWindowPos {
    // Keep at least 40 px of the window's top edge within the
    // viewport so the user can always grab the header back.
    const margin = 40;
    const maxX = window.innerWidth - margin;
    const maxY = window.innerHeight - margin;
    return {
      x: Math.max(margin - size.w, Math.min(maxX, p.x)),
      y: Math.max(0, Math.min(maxY, p.y)),
    };
  }

  function onHeaderPointerDown(e: PointerEvent): void {
    if (e.button !== 0) return;
    // Don't initiate a drag from buttons inside the header — they
    // need to receive their own click. The standard PointerEvent
    // path sees the button's onpointerdown (with stopPropagation) before
    // bubbling here, so this guards the bare-header case where a child
    // doesn't stop propagation.
    const target = e.target as HTMLElement | null;
    if (target && target.closest('button')) return;
    onRaise?.();
    dragging = true;
    dragStart = {
      pointerX: e.clientX,
      pointerY: e.clientY,
      startPosX: pos.x,
      startPosY: pos.y,
    };
    document.addEventListener('pointermove', onDragMove);
    document.addEventListener('pointerup', onDragEnd);
    document.addEventListener('pointercancel', onDragEnd);
  }

  function onDragMove(e: PointerEvent): void {
    if (!dragging || !dragStart) return;
    pos = clampPos({
      x: dragStart.startPosX + (e.clientX - dragStart.pointerX),
      y: dragStart.startPosY + (e.clientY - dragStart.pointerY),
    });
  }

  function onDragEnd(): void {
    if (!dragging) return;
    dragging = false;
    dragStart = null;
    document.removeEventListener('pointermove', onDragMove);
    document.removeEventListener('pointerup', onDragEnd);
    document.removeEventListener('pointercancel', onDragEnd);
  }

  function startResize(dir: ResizeDir, e: PointerEvent): void {
    if (e.button !== 0) return;
    e.stopPropagation();
    onRaise?.();
    resizing = {
      dir,
      pointerX: e.clientX,
      pointerY: e.clientY,
      startPosX: pos.x,
      startPosY: pos.y,
      startW: size.w,
      startH: size.h,
    };
    document.addEventListener('pointermove', onResizeMove);
    document.addEventListener('pointerup', onResizeEnd);
    document.addEventListener('pointercancel', onResizeEnd);
  }

  function onResizeMove(e: PointerEvent): void {
    const r = resizing;
    if (!r) return;
    const dx = e.clientX - r.pointerX;
    const dy = e.clientY - r.pointerY;

    let nextX = r.startPosX;
    let nextY = r.startPosY;
    let nextW = r.startW;
    let nextH = r.startH;

    if (r.dir.includes('e')) {
      nextW = Math.max(minSize.w, r.startW + dx);
    }
    if (r.dir.includes('s')) {
      nextH = Math.max(minSize.h, r.startH + dy);
    }
    if (r.dir.includes('w')) {
      const proposed = r.startW - dx;
      nextW = Math.max(minSize.w, proposed);
      nextX = r.startPosX + (r.startW - nextW);
    }
    if (r.dir.includes('n')) {
      const proposed = r.startH - dy;
      nextH = Math.max(minSize.h, proposed);
      nextY = r.startPosY + (r.startH - nextH);
    }

    size = { w: nextW, h: nextH };
    pos = { x: nextX, y: nextY };
  }

  function onResizeEnd(): void {
    if (!resizing) return;
    resizing = null;
    document.removeEventListener('pointermove', onResizeMove);
    document.removeEventListener('pointerup', onResizeEnd);
    document.removeEventListener('pointercancel', onResizeEnd);
  }

  // If the window unmounts mid-drag (e.g. consumer removes it while a
  // pointer is captured) the document listeners would otherwise keep
  // firing against stale closures. Mirror PartActionWindow's defensive
  // teardown.
  onDestroy(() => {
    onDragEnd();
    onResizeEnd();
  });

  function onWindowPointerDown(): void {
    onRaise?.();
  }
</script>

<section
  class="fw"
  class:fw--dragging={dragging}
  class:fw--resizing={resizing !== null}
  style="left: {pos.x}px; top: {pos.y}px; width: {size.w}px; height: {size.h}px; z-index: {z};"
  onpointerdown={onWindowPointerDown}
  aria-label={title || 'Window'}
>
  <header
    class="fw__header"
    role="toolbar"
    tabindex="-1"
    onpointerdown={onHeaderPointerDown}
  >
    {#if header}
      {@render header()}
    {:else}
      <h2 class="fw__title">{title}</h2>
    {/if}
    {#if onClose}
      <button
        type="button"
        class="fw__close"
        aria-label="Close window"
        onpointerdown={(e) => e.stopPropagation()}
        onclick={onClose}
      >×</button>
    {/if}
  </header>

  <div class="fw__body">
    {@render children?.()}
  </div>

  <!-- 8-way resize handles. Edge handles are 6 px thin strips along
       each edge; corner handles are 12 × 12 px squares stacked above.
       Pointer-events: auto on each so the body's content doesn't
       swallow drags that start inside the handle's bounds. role
       presentation since these are pure pointer affordances — screen-
       reader resize is not in scope for the HUD. -->
  <span role="presentation" class="fw__handle fw__handle--n"
        onpointerdown={(e) => startResize('n', e)}></span>
  <span role="presentation" class="fw__handle fw__handle--s"
        onpointerdown={(e) => startResize('s', e)}></span>
  <span role="presentation" class="fw__handle fw__handle--e"
        onpointerdown={(e) => startResize('e', e)}></span>
  <span role="presentation" class="fw__handle fw__handle--w"
        onpointerdown={(e) => startResize('w', e)}></span>
  <span role="presentation" class="fw__handle fw__handle--ne"
        onpointerdown={(e) => startResize('ne', e)}></span>
  <span role="presentation" class="fw__handle fw__handle--nw"
        onpointerdown={(e) => startResize('nw', e)}></span>
  <span role="presentation" class="fw__handle fw__handle--se"
        onpointerdown={(e) => startResize('se', e)}></span>
  <span role="presentation" class="fw__handle fw__handle--sw"
        onpointerdown={(e) => startResize('sw', e)}></span>
</section>

<style>
  .fw {
    position: fixed;
    display: flex;
    flex-direction: column;
    color: inherit;
    pointer-events: auto;
    user-select: none;
  }

  .fw__header {
    flex: 0 0 auto;
    display: flex;
    align-items: center;
    gap: 8px;
    cursor: grab;
    touch-action: none;
  }
  .fw--dragging .fw__header {
    cursor: grabbing;
  }

  .fw__title {
    flex: 1 1 auto;
    margin: 0;
    font: inherit;
    font-size: inherit;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .fw__close {
    flex: 0 0 auto;
    width: 18px;
    height: 18px;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    background: transparent;
    border: 0;
    color: inherit;
    cursor: pointer;
    font: inherit;
    line-height: 1;
  }

  .fw__body {
    flex: 1 1 auto;
    min-height: 0;
    overflow: auto;
  }

  /* Resize handles. Sized so a careless click near the edge still
     lands on the handle rather than the body. Cursors hint at the
     drag axis. Visually invisible — consumers can override `.fw` /
     `.fw__handle--*` selectors via :global if they want a visible
     edge treatment. */
  .fw__handle {
    position: absolute;
    background: transparent;
    touch-action: none;
  }
  .fw__handle--n  { top: -3px;    left: 6px;    right: 6px;   height: 6px;  cursor: ns-resize; }
  .fw__handle--s  { bottom: -3px; left: 6px;    right: 6px;   height: 6px;  cursor: ns-resize; }
  .fw__handle--e  { top: 6px;     bottom: 6px;  right: -3px;  width: 6px;   cursor: ew-resize; }
  .fw__handle--w  { top: 6px;     bottom: 6px;  left: -3px;   width: 6px;   cursor: ew-resize; }
  .fw__handle--ne { top: -4px;    right: -4px;  width: 12px;  height: 12px; cursor: nesw-resize; }
  .fw__handle--nw { top: -4px;    left: -4px;   width: 12px;  height: 12px; cursor: nwse-resize; }
  .fw__handle--se { bottom: -4px; right: -4px;  width: 12px;  height: 12px; cursor: nwse-resize; }
  .fw__handle--sw { bottom: -4px; left: -4px;   width: 12px;  height: 12px; cursor: nesw-resize; }

  .fw--resizing { user-select: none; }
</style>
