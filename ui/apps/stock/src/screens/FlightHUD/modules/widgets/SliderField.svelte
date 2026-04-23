<script lang="ts">
  // Float range slider (stock `UI_FloatRange`). Label above, native
  // range input below with min/max bounds + step. Value snaps to the
  // nearest step on input; the numeric readout sits at the right
  // edge of the label line so it reads as "LABEL ............ 78%"
  // even while dragging.
  //
  // Wire: value read + write as a plain number (server casts to the
  // field's declared float/double/int type).

  import type { PartFieldData } from '@dragonglass/telemetry/core';

  interface Props {
    field: Extract<PartFieldData, { kind: 'slider' }>;
    onSetField: (fieldId: string, value: boolean | number) => void;
  }

  const { field, onSetField }: Props = $props();

  // Decimals heuristic: step size sets the formatting precision.
  // 0.1 → one decimal; 0.01 → two; integers → none.
  const decimals = $derived.by(() => {
    if (field.step >= 1) return 0;
    if (field.step >= 0.1) return 1;
    if (field.step >= 0.01) return 2;
    return 3;
  });
  const valueStr = $derived(field.value.toFixed(decimals));

  function onInput(e: Event): void {
    const next = parseFloat((e.target as HTMLInputElement).value);
    if (!Number.isFinite(next)) return;
    onSetField(field.id, next);
  }
</script>

<div class="widget widget--slider">
  <div class="widget__head">
    <span class="widget__label">{field.label}</span>
    <span class="widget__value">{valueStr}</span>
  </div>
  <input
    class="widget__range"
    type="range"
    min={field.min}
    max={field.max}
    step={field.step || 'any'}
    value={field.value}
    oninput={onInput}
    onpointerdown={(e) => e.stopPropagation()}
  />
</div>

<style>
  .widget {
    display: flex;
    flex-direction: column;
    gap: 2px;
    min-height: 28px;
    font-size: 9px;
  }

  .widget__head {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 6px;
  }

  .widget__label {
    color: var(--fg-dim);
    letter-spacing: 0.06em;
    text-transform: uppercase;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .widget__value {
    color: var(--accent);
    font-variant-numeric: tabular-nums;
    font-family: var(--font-display);
    text-shadow: 0 0 4px var(--accent-glow);
  }

  /* Native range — restyled for phosphor theme. -webkit prefix for
     WebKit-based CEF; Chromium accepts both prefixed and
     ::-moz-range-thumb but we only target WebKit here. */
  .widget__range {
    -webkit-appearance: none;
    appearance: none;
    width: 100%;
    height: 14px;
    background: transparent;
    cursor: pointer;
  }
  .widget__range::-webkit-slider-runnable-track {
    height: 4px;
    background: rgba(46, 106, 85, 0.35);
    border: 1px solid var(--line);
  }
  .widget__range::-webkit-slider-thumb {
    -webkit-appearance: none;
    width: 12px;
    height: 12px;
    margin-top: -5px;
    background: var(--accent);
    border: 1px solid var(--bg);
    box-shadow: 0 0 6px var(--accent-glow);
  }
</style>
