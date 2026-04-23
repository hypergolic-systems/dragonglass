<script lang="ts">
  // Bespoke renderer for ModuleResourceConverter (ISRU, fuel cells,
  // labs). One module per mode on stock parts, so each mode renders
  // as its own row — letting the pilot toggle LF vs Ox vs MP
  // independently on a stock ISRU.
  //
  // Shape mirrors ModuleGenerator: active / alwaysOn badge, inputs
  // + outputs ledger, status string, Activate/Shutdown button.
  //
  // Actions: invokeEvent('StartResourceConverter' | 'StopResourceConverter').

  import type { ModuleRendererProps } from './types';
  import type { PartModuleResourceConverter } from '@dragonglass/telemetry/core';

  const { module, onInvokeEvent }: ModuleRendererProps = $props();
  const conv = $derived(module as PartModuleResourceConverter);
</script>

<section class="conv">
  <header class="conv__head">
    <span class="conv__name">CONVERTER</span>
    <span class="conv__badge" data-tone={conv.active ? 'active' : 'off'}>
      {conv.active ? 'Running' : 'Idle'}
    </span>
  </header>

  {#if conv.converterName && conv.converterName.length > 0}
    <div class="conv__title">{conv.converterName}</div>
  {/if}

  {#if conv.inputs.length > 0}
    <div class="conv__label">Input</div>
    {#each conv.inputs as flow (flow.name)}
      <div class="conv__row">
        <span class="conv__res-name">{flow.name}</span>
        <span class="conv__res-rate conv__res-rate--neg">
          −{flow.rate.toFixed(3)}<em>/s</em>
        </span>
      </div>
    {/each}
  {/if}

  {#if conv.outputs.length > 0}
    <div class="conv__label conv__label--outputs">Output</div>
    {#each conv.outputs as flow (flow.name)}
      <div class="conv__row">
        <span class="conv__res-name">{flow.name}</span>
        <span class="conv__res-rate conv__res-rate--pos">
          +{flow.rate.toFixed(3)}<em>/s</em>
        </span>
      </div>
    {/each}
  {/if}

  {#if conv.status && conv.status.length > 0}
    <div class="conv__status">{conv.status}</div>
  {/if}

  <div class="conv__events">
    <button
      type="button"
      class="conv__event"
      onclick={() => onInvokeEvent(conv.active ? 'StopResourceConverter' : 'StartResourceConverter')}
      onpointerdown={(e) => e.stopPropagation()}
    >{conv.active ? 'Shutdown' : 'Activate'}</button>
  </div>
</section>

<style>
  .conv {
    margin-top: 8px;
    padding-top: 7px;
    border-top: 1px solid var(--line);
  }
  .conv__head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    margin-bottom: 3px;
  }
  .conv__name {
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.22em;
    color: var(--fg-dim);
    text-transform: uppercase;
  }
  .conv__badge {
    padding: 1px 6px;
    font-family: var(--font-mono);
    font-size: 8px;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: var(--fg-mute);
    border: 1px solid var(--line);
  }
  .conv__badge[data-tone='active'] {
    color: var(--accent);
    border-color: var(--accent);
    background: rgba(126, 245, 184, 0.12);
    text-shadow: 0 0 3px var(--accent-glow);
  }

  .conv__title {
    font-family: var(--font-mono);
    font-size: 9px;
    letter-spacing: 0.06em;
    color: var(--fg);
    margin-bottom: 4px;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .conv__label {
    font-family: var(--font-mono);
    font-size: 8px;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: var(--fg-mute);
    margin-bottom: 1px;
  }
  .conv__label--outputs { margin-top: 4px; }

  .conv__row {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 6px;
    font-size: 9px;
    min-height: 12px;
    margin-bottom: 1px;
  }
  .conv__res-name {
    color: var(--fg);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .conv__res-rate {
    font-family: var(--font-display);
    font-variant-numeric: tabular-nums;
  }
  .conv__res-rate--pos {
    color: var(--accent);
    text-shadow: 0 0 4px var(--accent-glow);
  }
  .conv__res-rate--neg {
    color: var(--warn);
  }
  .conv__res-rate em {
    font-style: normal;
    color: var(--fg-mute);
    font-size: 8px;
    margin-left: 2px;
  }

  .conv__status {
    font-family: var(--font-mono);
    font-size: 8px;
    color: var(--fg-dim);
    letter-spacing: 0.06em;
    margin-top: 3px;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .conv__events { display: flex; margin-top: 5px; }
  .conv__event {
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
  .conv__event:hover {
    background: rgba(126, 245, 184, 0.16);
    border-color: var(--accent);
  }
</style>
