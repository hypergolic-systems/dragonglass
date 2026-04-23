<script lang="ts">
  // Bespoke renderer for ModuleActiveRadiator.
  //
  // Pumped cooler — draws power, moves heat. Stock exposes a single
  // on/off (Activate / Shutdown) plus a short status string
  // ("Nominal", "Off", "No Core Heat") and the peak transfer rate in
  // kW. We render all three and a power button.
  //
  // Actions: invokeEvent('Activate' | 'Shutdown').

  import type { ModuleRendererProps } from './types';
  import type { PartModuleActiveRadiator } from '@dragonglass/telemetry/core';

  const { module, onInvokeEvent }: ModuleRendererProps = $props();
  const rad = $derived(module as PartModuleActiveRadiator);
</script>

<section class="arad">
  <header class="arad__head">
    <span class="arad__name">COOLER</span>
    <span class="arad__badge" data-tone={rad.isCooling ? 'active' : 'off'}>
      {rad.isCooling ? 'Cooling' : 'Off'}
    </span>
  </header>

  <div class="arad__row">
    <span class="arad__label">Max Flow</span>
    <span class="arad__value">
      {rad.maxTransfer.toFixed(0)}<em>kW</em>
    </span>
  </div>

  {#if rad.status && rad.status.length > 0}
    <div class="arad__status">{rad.status}</div>
  {/if}

  <div class="arad__events">
    <button
      type="button"
      class="arad__event"
      onclick={() => onInvokeEvent(rad.isCooling ? 'Shutdown' : 'Activate')}
      onpointerdown={(e) => e.stopPropagation()}
    >{rad.isCooling ? 'Shutdown' : 'Activate'}</button>
  </div>
</section>

<style>
  .arad {
    margin-top: 8px;
    padding-top: 7px;
    border-top: 1px solid var(--line);
  }
  .arad__head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    margin-bottom: 5px;
  }
  .arad__name {
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.22em;
    color: var(--fg-dim);
    text-transform: uppercase;
  }
  .arad__badge {
    padding: 1px 6px;
    font-family: var(--font-mono);
    font-size: 8px;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: var(--fg-mute);
    border: 1px solid var(--line);
  }
  .arad__badge[data-tone='active'] {
    color: var(--info);
    border-color: var(--info);
    background: rgba(90, 176, 255, 0.12);
    text-shadow: 0 0 3px var(--info-glow);
  }

  .arad__row {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 6px;
    font-size: 9px;
    min-height: 13px;
    margin-bottom: 2px;
  }
  .arad__label {
    color: var(--fg-dim);
    letter-spacing: 0.06em;
    text-transform: uppercase;
    font-size: 8px;
  }
  .arad__value {
    color: var(--info);
    font-family: var(--font-display);
    font-variant-numeric: tabular-nums;
    text-shadow: 0 0 4px var(--info-glow);
  }
  .arad__value em {
    font-style: normal;
    color: var(--fg-mute);
    font-size: 8px;
    margin-left: 3px;
  }
  .arad__status {
    font-family: var(--font-mono);
    font-size: 8px;
    color: var(--fg-dim);
    letter-spacing: 0.06em;
    margin: 2px 0 4px;
  }
  .arad__events { display: flex; }
  .arad__event {
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
  .arad__event:hover {
    background: rgba(126, 245, 184, 0.16);
    border-color: var(--accent);
  }
</style>
