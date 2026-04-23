<script lang="ts">
  // Boolean toggle (stock `UI_Toggle`). Shows the field name on the
  // left and the current state-labelled pill on the right. Clicking
  // the pill flips the value, which the server round-trips back and
  // the next frame renders the updated state.

  import type { PartFieldData } from '@dragonglass/telemetry/core';

  interface Props {
    field: Extract<PartFieldData, { kind: 'toggle' }>;
    onSetField: (fieldId: string, value: boolean | number) => void;
  }

  const { field, onSetField }: Props = $props();

  const stateLabel = $derived(
    field.value ? field.enabledText || 'On' : field.disabledText || 'Off',
  );
</script>

<div class="widget widget--toggle">
  <span class="widget__label">{field.label}</span>
  <button
    type="button"
    class="widget__toggle"
    class:widget__toggle--on={field.value}
    onclick={() => onSetField(field.id, !field.value)}
    onpointerdown={(e) => e.stopPropagation()}
  >{stateLabel}</button>
</div>

<style>
  .widget {
    display: flex;
    align-items: center;
    gap: 8px;
    min-height: 18px;
    font-size: 9px;
  }

  .widget__label {
    flex: 1 1 auto;
    color: var(--fg-dim);
    letter-spacing: 0.06em;
    text-transform: uppercase;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .widget__toggle {
    flex: 0 0 auto;
    padding: 2px 10px;
    min-width: 58px;
    font-family: var(--font-mono);
    font-size: 9px;
    letter-spacing: 0.08em;
    text-transform: uppercase;
    color: var(--fg-dim);
    background: transparent;
    border: 1px solid var(--line-bright);
    cursor: pointer;
    transition: color 160ms ease, background 160ms ease, border-color 160ms ease;
  }
  .widget__toggle:hover {
    border-color: var(--fg-dim);
  }

  .widget__toggle--on {
    color: var(--accent);
    background: rgba(126, 245, 184, 0.12);
    border-color: var(--accent);
    text-shadow: 0 0 4px var(--accent-glow);
  }
  .widget__toggle--on:hover {
    background: rgba(126, 245, 184, 0.22);
  }
</style>
