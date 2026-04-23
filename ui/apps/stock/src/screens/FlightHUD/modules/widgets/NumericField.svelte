<script lang="ts">
  // Numeric field with coarse/fine buttons (stock `UI_FloatEdit`).
  // Six buttons flank a read-out: −− − − value + + ++ (large,
  // small, small/large deltas). The slide increment is used for
  // the inline range slider; we expose it alongside so both paths
  // work. Stock uses the same pattern for things like vernier gimbal
  // authority tuning.
  //
  // Wire: value written as a number; server casts to the field type.

  import type { PartFieldData } from '@dragonglass/telemetry/core';

  interface Props {
    field: Extract<PartFieldData, { kind: 'numeric' }>;
    onSetField: (fieldId: string, value: boolean | number) => void;
  }

  const { field, onSetField }: Props = $props();

  // Mirror SliderField: step guides decimal formatting.
  const decimals = $derived.by(() => {
    const s = Math.min(
      field.incSlide || Infinity,
      field.incSmall || Infinity,
    );
    if (!Number.isFinite(s) || s >= 1) return 0;
    if (s >= 0.1) return 1;
    if (s >= 0.01) return 2;
    return 3;
  });
  const valueStr = $derived(field.value.toFixed(decimals));

  function clamp(v: number): number {
    const lo = Number.isFinite(field.min) ? field.min : -Infinity;
    const hi = Number.isFinite(field.max) ? field.max : Infinity;
    return Math.max(lo, Math.min(hi, v));
  }

  function step(delta: number): void {
    const next = clamp(field.value + delta);
    if (next === field.value) return;
    onSetField(field.id, next);
  }

  function onSlide(e: Event): void {
    const next = parseFloat((e.target as HTMLInputElement).value);
    if (!Number.isFinite(next)) return;
    onSetField(field.id, next);
  }
</script>

<div class="widget widget--numeric">
  <div class="widget__head">
    <span class="widget__label">{field.label}</span>
    <span class="widget__value">
      {valueStr}{#if field.unit}<em>{field.unit}</em>{/if}
    </span>
  </div>
  <div class="widget__row">
    {#if field.incLarge}
      <button class="widget__btn" onclick={() => step(-field.incLarge)} onpointerdown={(e) => e.stopPropagation()}>−−</button>
    {/if}
    {#if field.incSmall}
      <button class="widget__btn" onclick={() => step(-field.incSmall)} onpointerdown={(e) => e.stopPropagation()}>−</button>
    {/if}
    <input
      class="widget__range"
      type="range"
      min={Number.isFinite(field.min) ? field.min : -1000}
      max={Number.isFinite(field.max) ? field.max : 1000}
      step={field.incSlide || 'any'}
      value={field.value}
      oninput={onSlide}
      onpointerdown={(e) => e.stopPropagation()}
    />
    {#if field.incSmall}
      <button class="widget__btn" onclick={() => step(field.incSmall)} onpointerdown={(e) => e.stopPropagation()}>+</button>
    {/if}
    {#if field.incLarge}
      <button class="widget__btn" onclick={() => step(field.incLarge)} onpointerdown={(e) => e.stopPropagation()}>++</button>
    {/if}
  </div>
</div>

<style>
  .widget {
    display: flex;
    flex-direction: column;
    gap: 2px;
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
    font-family: var(--font-display);
    font-variant-numeric: tabular-nums;
    text-shadow: 0 0 4px var(--accent-glow);
  }
  .widget__value em {
    color: var(--fg-mute);
    font-style: normal;
    font-size: 8px;
    letter-spacing: 0.08em;
    margin-left: 2px;
  }

  .widget__row {
    display: flex;
    align-items: center;
    gap: 3px;
  }

  .widget__btn {
    flex: 0 0 auto;
    width: 18px;
    height: 18px;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    font-family: var(--font-mono);
    font-size: 9px;
    color: var(--fg-dim);
    background: transparent;
    border: 1px solid var(--line-bright);
    cursor: pointer;
    transition: color 160ms ease, background 160ms ease, border-color 160ms ease;
  }
  .widget__btn:hover {
    color: var(--accent);
    background: rgba(126, 245, 184, 0.1);
    border-color: var(--accent);
  }

  .widget__range {
    -webkit-appearance: none;
    appearance: none;
    flex: 1 1 auto;
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
    width: 10px;
    height: 10px;
    margin-top: -4px;
    background: var(--accent);
    border: 1px solid var(--bg);
    box-shadow: 0 0 4px var(--accent-glow);
  }
</style>
