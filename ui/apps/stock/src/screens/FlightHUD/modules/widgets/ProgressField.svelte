<script lang="ts">
  // Read-only progress bar (stock `UI_ProgressBar`). Typical
  // consumers: engine ignition counter, science-experiment progress,
  // robotic part travel %, kerbal hunger / deep-space-network
  // signal strength.

  import type { PartFieldData } from '@dragonglass/telemetry/core';

  interface Props {
    field: Extract<PartFieldData, { kind: 'progress' }>;
  }

  const { field }: Props = $props();

  const span = $derived(field.max - field.min);
  const pct = $derived.by(() => {
    if (!Number.isFinite(span) || span <= 0) return 0;
    const n = (field.value - field.min) / span;
    return Math.max(0, Math.min(1, n)) * 100;
  });
</script>

<div class="widget widget--progress">
  <div class="widget__head">
    <span class="widget__label">{field.label}</span>
    <span class="widget__value">{field.value.toFixed(span >= 10 ? 0 : 1)}</span>
  </div>
  <div class="widget__bar" role="presentation">
    <div class="widget__bar-fill" style="--pct: {pct}%"></div>
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

  .widget__bar {
    position: relative;
    height: 4px;
    background: rgba(46, 106, 85, 0.2);
    border: 1px solid var(--line);
  }
  .widget__bar-fill {
    position: absolute;
    inset: 0 auto 0 0;
    width: var(--pct, 0%);
    background: var(--accent);
    box-shadow: 0 0 5px var(--accent-glow);
    transition: width 240ms ease;
  }
</style>
