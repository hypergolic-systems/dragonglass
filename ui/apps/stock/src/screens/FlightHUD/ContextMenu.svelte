<script lang="ts">
  // Generic fixed-position menu. Pops up at a client-coordinate
  // position, renders a list of actionable items, and dismisses on
  // outside-click / Esc / item-select. Reused across the HUD for
  // right-click interactions.
  //
  // Positioning is fixed-to-viewport, not relative to any scroll
  // container — so the menu stays anchored to where the user
  // right-clicked even inside a scrolled staging stack. When the
  // requested position would overflow the viewport we nudge back in.
  //
  // Keyboard. ↑/↓ move focus, Enter activates, Esc dismisses.
  // `role="menu"` + `role="menuitem"` so screen readers treat it as
  // a popup menu rather than a free-floating div.

  export interface MenuItem {
    readonly label: string;
    readonly disabled?: boolean;
    readonly danger?: boolean;
    /** Fires on click or Enter. The component also dismisses after
     *  invocation — callers should not call the dismiss function
     *  themselves. */
    onSelect(): void;
  }

  let {
    items,
    x,
    y,
    onDismiss,
  }: {
    items: readonly MenuItem[];
    x: number;
    y: number;
    onDismiss: () => void;
  } = $props();

  let panel = $state<HTMLElement | null>(null);
  let focusIndex = $state(0);

  // Clamp within the viewport. Initial value mirrors the requested
  // (x, y); the $effect below refines after the panel has mounted
  // so we know its real size. Initialised to 0 to sidestep Svelte's
  // "state captures initial prop value" warning — the effect runs
  // synchronously on mount before any paint, so the 0/0 flash is
  // invisible.
  let left = $state(0);
  let top = $state(0);
  $effect(() => {
    if (!panel) return;
    const rect = panel.getBoundingClientRect();
    const vw = window.innerWidth;
    const vh = window.innerHeight;
    const pad = 4;
    left = Math.max(pad, Math.min(x, vw - rect.width - pad));
    top = Math.max(pad, Math.min(y, vh - rect.height - pad));
    // Push initial keyboard focus onto the first enabled item.
    const first = items.findIndex((it) => !it.disabled);
    focusIndex = first >= 0 ? first : 0;
    // Focus the panel so key events land on it.
    panel.focus({ preventScroll: true });
  });

  function activate(i: number): void {
    const item = items[i];
    if (!item || item.disabled) return;
    onDismiss();
    item.onSelect();
  }

  function moveFocus(delta: number): void {
    if (items.length === 0) return;
    // Step over disabled items.
    let next = focusIndex;
    for (let n = 0; n < items.length; n++) {
      next = (next + delta + items.length) % items.length;
      if (!items[next].disabled) {
        focusIndex = next;
        return;
      }
    }
  }

  function handleKey(e: KeyboardEvent): void {
    switch (e.key) {
      case 'Escape':
        e.preventDefault();
        onDismiss();
        break;
      case 'ArrowDown':
        e.preventDefault();
        moveFocus(1);
        break;
      case 'ArrowUp':
        e.preventDefault();
        moveFocus(-1);
        break;
      case 'Enter':
      case ' ':
        e.preventDefault();
        activate(focusIndex);
        break;
    }
  }

  // Outside-click dismissal. Bound on mount via `$effect`; `capture`
  // mode so we see the mousedown before it retargets focus elsewhere.
  $effect(() => {
    const onDown = (e: MouseEvent) => {
      if (panel && !panel.contains(e.target as Node)) {
        onDismiss();
      }
    };
    document.addEventListener('mousedown', onDown, true);
    return () => document.removeEventListener('mousedown', onDown, true);
  });
</script>

<!-- svelte-ignore a11y_no_noninteractive_tabindex -->
<div
  class="ctx-menu"
  role="menu"
  tabindex="-1"
  bind:this={panel}
  style:left="{left}px"
  style:top="{top}px"
  onkeydown={handleKey}
  oncontextmenu={(e) => e.preventDefault()}
>
  {#each items as item, i}
    <button
      type="button"
      class="ctx-menu__item"
      class:ctx-menu__item--focused={i === focusIndex}
      class:ctx-menu__item--disabled={item.disabled}
      class:ctx-menu__item--danger={item.danger}
      disabled={item.disabled}
      role="menuitem"
      tabindex="-1"
      onmouseenter={() => { if (!item.disabled) focusIndex = i; }}
      onclick={() => activate(i)}
    >
      {item.label}
    </button>
  {/each}
</div>

<style>
  .ctx-menu {
    position: fixed;
    z-index: 100;
    min-width: 160px;
    padding: 3px 0;
    background: var(--bg-panel-solid);
    border: 1px solid var(--line-accent);
    box-shadow:
      0 0 22px rgba(126, 245, 184, 0.06),
      inset 0 0 0 1px rgba(126, 245, 184, 0.04),
      0 8px 24px rgba(0, 0, 0, 0.6);
    font-family: var(--font-mono);
    font-size: 11px;
    color: var(--fg);
    outline: none;
    animation: ctxIn 140ms cubic-bezier(0.2, 0.8, 0.25, 1);
  }

  @keyframes ctxIn {
    from {
      opacity: 0;
      transform: translateY(-3px);
    }
    to {
      opacity: 1;
      transform: translateY(0);
    }
  }

  .ctx-menu__item {
    display: block;
    width: 100%;
    padding: 4px 10px;
    text-align: left;
    background: transparent;
    border: none;
    color: inherit;
    font: inherit;
    letter-spacing: 0.04em;
    cursor: pointer;
    transition: background 120ms ease, color 120ms ease;
  }

  .ctx-menu__item--focused {
    background: rgba(126, 245, 184, 0.12);
    color: var(--accent);
  }

  .ctx-menu__item--danger.ctx-menu__item--focused {
    background: rgba(255, 82, 82, 0.14);
    color: var(--alert);
  }

  .ctx-menu__item--disabled {
    color: var(--fg-mute);
    cursor: default;
  }
  .ctx-menu__item--disabled.ctx-menu__item--focused {
    background: transparent;
    color: var(--fg-mute);
  }
</style>
