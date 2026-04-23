<script lang="ts">
  // Select-one-of-many (stock `UI_ChooseOption`). Renders as the
  // current choice flanked by ◀ / ▶ chevrons that advance to the
  // previous / next option — matches the stock PAW affordance.
  // Wrapping at both ends makes the control feel circular.
  //
  // Wire: server emits `selectedIndex` and `display[]`; write-back is
  // the new index, which the server maps to the actual field value.

  import type { PartFieldData } from '@dragonglass/telemetry/core';

  interface Props {
    field: Extract<PartFieldData, { kind: 'option' }>;
    onSetField: (fieldId: string, value: boolean | number) => void;
  }

  const { field, onSetField }: Props = $props();

  const count = $derived(field.display.length);
  const currentText = $derived.by(() => {
    if (field.selectedIndex < 0 || field.selectedIndex >= count) return '—';
    return field.display[field.selectedIndex];
  });

  function step(delta: number): void {
    if (count === 0) return;
    // Guard against a stale -1 selectedIndex: start the step from 0.
    const base = field.selectedIndex < 0 ? 0 : field.selectedIndex;
    const next = (base + delta + count) % count;
    if (next === field.selectedIndex) return;
    onSetField(field.id, next);
  }
</script>

<div class="widget widget--option">
  <span class="widget__label">{field.label}</span>
  <div class="widget__chooser">
    <button
      type="button"
      class="widget__step"
      aria-label="Previous"
      disabled={count === 0}
      onclick={() => step(-1)}
      onpointerdown={(e) => e.stopPropagation()}
    >◀</button>
    <span class="widget__current">{currentText}</span>
    <button
      type="button"
      class="widget__step"
      aria-label="Next"
      disabled={count === 0}
      onclick={() => step(1)}
      onpointerdown={(e) => e.stopPropagation()}
    >▶</button>
  </div>
</div>

<style>
  .widget {
    display: flex;
    flex-direction: column;
    gap: 3px;
    font-size: 9px;
  }

  .widget__label {
    color: var(--fg-dim);
    letter-spacing: 0.06em;
    text-transform: uppercase;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .widget__chooser {
    display: flex;
    align-items: center;
    gap: 4px;
    border: 1px solid var(--line-bright);
    background: rgba(46, 106, 85, 0.08);
    min-height: 20px;
  }

  .widget__step {
    flex: 0 0 auto;
    width: 18px;
    height: 18px;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    font-size: 9px;
    color: var(--fg-dim);
    background: transparent;
    border: none;
    cursor: pointer;
    transition: color 160ms ease, background 160ms ease;
  }
  .widget__step:hover:not(:disabled) {
    color: var(--accent);
    background: rgba(126, 245, 184, 0.12);
  }
  .widget__step:disabled {
    opacity: 0.3;
    cursor: default;
  }

  .widget__current {
    flex: 1 1 auto;
    text-align: center;
    color: var(--accent);
    font-family: var(--font-display);
    font-size: 10px;
    letter-spacing: 0.08em;
    text-transform: uppercase;
    text-shadow: 0 0 4px var(--accent-glow);
    padding: 0 2px;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
</style>
