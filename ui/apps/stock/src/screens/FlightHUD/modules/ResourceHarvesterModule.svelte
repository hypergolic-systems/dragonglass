<script lang="ts">
  // Bespoke renderer for ModuleResourceHarvester (the drill family).
  //
  // Drills are their own thing: pick a biome-keyed abundance, run the
  // drill, watch efficiency vs thermal load. The hero is an abundance
  // bar with the percentage number, then the current Efficiency
  // readout, then an Activate/Shutdown button.
  //
  // Actions: invokeEvent('StartResourceConverter' | 'StopResourceConverter').

  import type { ModuleRendererProps } from './types';
  import type {
    PartModuleResourceHarvester,
    HarvesterType,
  } from '@dragonglass/telemetry/core';

  const TYPE_LABEL: Record<HarvesterType, string> = {
    planetary: 'Surface',
    oceanic: 'Ocean',
    atmospheric: 'Atmos',
    exospheric: 'Exo',
  };

  const { module, onInvokeEvent }: ModuleRendererProps = $props();
  const harv = $derived(module as PartModuleResourceHarvester);
  const abundancePct = $derived(Math.max(0, Math.min(1, harv.abundance)) * 100);
  const typeLabel = $derived(TYPE_LABEL[harv.harvesterType] ?? 'Drill');
</script>

<section class="harv">
  <header class="harv__head">
    <span class="harv__name">DRILL · {typeLabel}</span>
    <span class="harv__badge" data-tone={harv.active ? 'active' : 'off'}>
      {harv.active ? 'Running' : 'Idle'}
    </span>
  </header>

  <div class="harv__row">
    <span class="harv__label">Resource</span>
    <span class="harv__value harv__value--text">{harv.resourceName || '—'}</span>
  </div>

  <div class="harv__row">
    <span class="harv__label">Abundance</span>
    <span class="harv__value">{abundancePct.toFixed(2)}<em>%</em></span>
  </div>
  <div class="harv__bar" role="presentation">
    <div class="harv__bar-fill" style="--pct: {abundancePct}%"></div>
  </div>

  {#if harv.status && harv.status.length > 0}
    <div class="harv__status">{harv.status}</div>
  {/if}

  <div class="harv__events">
    <button
      type="button"
      class="harv__event"
      onclick={() => onInvokeEvent(harv.active ? 'StopResourceConverter' : 'StartResourceConverter')}
      onpointerdown={(e) => e.stopPropagation()}
    >{harv.active ? 'Shutdown Drill' : 'Start Drill'}</button>
  </div>
</section>

<style>
  .harv {
    margin-top: 8px;
    padding-top: 7px;
    border-top: 1px solid var(--line);
  }
  .harv__head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    margin-bottom: 5px;
  }
  .harv__name {
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.22em;
    color: var(--fg-dim);
    text-transform: uppercase;
  }
  .harv__badge {
    padding: 1px 6px;
    font-family: var(--font-mono);
    font-size: 8px;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: var(--fg-mute);
    border: 1px solid var(--line);
  }
  .harv__badge[data-tone='active'] {
    color: var(--accent);
    border-color: var(--accent);
    background: rgba(126, 245, 184, 0.12);
    text-shadow: 0 0 3px var(--accent-glow);
  }

  .harv__row {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 6px;
    font-size: 9px;
    min-height: 12px;
    margin-bottom: 1px;
  }
  .harv__label {
    color: var(--fg-dim);
    letter-spacing: 0.06em;
    text-transform: uppercase;
    font-size: 8px;
  }
  .harv__value {
    color: var(--accent);
    font-family: var(--font-display);
    font-variant-numeric: tabular-nums;
    text-shadow: 0 0 4px var(--accent-glow);
  }
  .harv__value--text {
    color: var(--fg);
    text-shadow: none;
    font-family: var(--font-display);
    font-size: 10px;
  }
  .harv__value em {
    font-style: normal;
    color: var(--fg-mute);
    font-size: 8px;
    margin-left: 3px;
  }

  .harv__bar {
    position: relative;
    height: 4px;
    background: rgba(46, 106, 85, 0.2);
    border: 1px solid var(--line);
    margin: 1px 0 4px;
  }
  .harv__bar-fill {
    position: absolute;
    inset: 0 auto 0 0;
    width: var(--pct, 0%);
    background: var(--accent);
    box-shadow: 0 0 5px var(--accent-glow);
    transition: width 180ms ease;
  }

  .harv__status {
    font-family: var(--font-mono);
    font-size: 8px;
    color: var(--fg-dim);
    letter-spacing: 0.06em;
    margin: 2px 0 4px;
  }

  .harv__events { display: flex; margin-top: 4px; }
  .harv__event {
    flex: 1 1 auto;
    padding: 3px 8px;
    min-height: 22px;
    font-family: var(--font-mono);
    font-size: 9px;
    letter-spacing: 0.08em;
    text-transform: uppercase;
    color: var(--accent);
    background: rgba(126, 245, 184, 0.06);
    border: 1px solid var(--line-accent);
    cursor: pointer;
  }
  .harv__event:hover {
    background: rgba(126, 245, 184, 0.16);
    border-color: var(--accent);
  }
</style>
