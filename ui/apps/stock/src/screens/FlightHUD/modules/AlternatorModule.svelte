<script lang="ts">
  // Bespoke renderer for ModuleAlternator. Purely passive — alternator
  // output scales with the sibling engine's throttle. Renderer shows
  // the max rate and dims when the engine's idle.
  //
  // No actions — ModuleAlternator has no PAW events.

  import type { ModuleRendererProps } from './types';
  import type { PartModuleAlternator } from '@dragonglass/telemetry/core';

  const { module }: ModuleRendererProps = $props();
  const alt = $derived(module as PartModuleAlternator);
  const label = $derived(alt.outputName || 'Output');
  const units = $derived(alt.outputUnits || '');
</script>

<section class="alt" class:alt--idle={!alt.engineRunning}>
  <header class="alt__head">
    <span class="alt__name">ALTERNATOR</span>
    <span class="alt__badge" data-tone={alt.engineRunning ? 'active' : 'off'}>
      {alt.engineRunning ? 'Charging' : 'Idle'}
    </span>
  </header>

  <div class="alt__row">
    <span class="alt__label">{label}</span>
    <span class="alt__value">
      {alt.outputRate.toFixed(2)}<em>{units}</em>
    </span>
  </div>
</section>

<style>
  .alt {
    margin-top: 8px;
    padding-top: 7px;
    border-top: 1px solid var(--line);
  }
  .alt--idle .alt__value {
    opacity: 0.4;
  }
  .alt__head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    margin-bottom: 5px;
  }
  .alt__name {
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.22em;
    color: var(--fg-dim);
    text-transform: uppercase;
  }
  .alt__badge {
    padding: 1px 6px;
    font-family: var(--font-mono);
    font-size: 8px;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: var(--fg-mute);
    border: 1px solid var(--line);
  }
  .alt__badge[data-tone='active'] {
    color: var(--accent);
    border-color: var(--accent);
    background: rgba(126, 245, 184, 0.12);
    text-shadow: 0 0 3px var(--accent-glow);
  }

  .alt__row {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 6px;
    font-size: 9px;
    min-height: 13px;
  }
  .alt__label {
    color: var(--fg-dim);
    letter-spacing: 0.06em;
    text-transform: uppercase;
    font-size: 8px;
  }
  .alt__value {
    color: var(--accent);
    font-family: var(--font-display);
    font-variant-numeric: tabular-nums;
    text-shadow: 0 0 4px var(--accent-glow);
    transition: opacity 180ms ease;
  }
  .alt__value em {
    font-style: normal;
    color: var(--fg-mute);
    font-size: 8px;
    margin-left: 3px;
  }
</style>
