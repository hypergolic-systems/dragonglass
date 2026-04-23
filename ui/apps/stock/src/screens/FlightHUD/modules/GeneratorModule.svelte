<script lang="ts">
  // Bespoke renderer for ModuleGenerator — RTGs (always-on), fuel
  // cells (toggleable with an input cost), and any mod-added
  // generator. Shows the resource ledger (inputs below, outputs
  // above) with the signed rates, an efficiency % bar, and a status
  // string (stock "Nominal" / "Lacking: LF" / etc.).

  import type { ModuleRendererProps } from './types';
  import type { PartModuleGenerator } from '@dragonglass/telemetry/core';

  const { module, onInvokeEvent }: ModuleRendererProps = $props();
  const gen = $derived(module as PartModuleGenerator);

  const badgeTone = $derived(
    !gen.active ? 'off'
    : gen.efficiency < 0.01 ? 'warn'
    : 'active',
  );
  const badgeLabel = $derived(
    gen.alwaysOn ? 'Always On'
    : gen.active ? 'Running'
    : 'Off',
  );

  // Efficiency is 0..1 in stock; we render a bar + percent.
  const effPct = $derived(Math.max(0, Math.min(1, gen.efficiency)) * 100);
</script>

<section class="gen">
  <header class="gen__head">
    <span class="gen__name">GENERATOR</span>
    <span class="gen__badge" data-tone={badgeTone}>{badgeLabel}</span>
  </header>

  {#if gen.outputs.length > 0}
    <div class="gen__label">Output</div>
    {#each gen.outputs as flow (flow.name)}
      <div class="gen__row">
        <span class="gen__res-name">{flow.name}</span>
        <span class="gen__res-rate gen__res-rate--pos">
          +{flow.rate.toFixed(3)}<em>/s</em>
        </span>
      </div>
    {/each}
  {/if}

  {#if gen.inputs.length > 0}
    <div class="gen__label gen__label--inputs">Input</div>
    {#each gen.inputs as flow (flow.name)}
      <div class="gen__row">
        <span class="gen__res-name">{flow.name}</span>
        <span class="gen__res-rate gen__res-rate--neg">
          −{flow.rate.toFixed(3)}<em>/s</em>
        </span>
      </div>
    {/each}
  {/if}

  <div class="gen__eff-row">
    <span class="gen__eff-label">Efficiency</span>
    <span class="gen__eff-value">{effPct.toFixed(0)}<em>%</em></span>
  </div>
  <div class="gen__bar" role="presentation">
    <div class="gen__bar-fill" style="--pct: {effPct}%"></div>
  </div>

  {#if gen.status && gen.status.length > 0}
    <div class="gen__status">{gen.status}</div>
  {/if}

  {#if !gen.alwaysOn}
    <div class="gen__events">
      <button
        type="button"
        class="gen__event"
        onclick={() => onInvokeEvent(gen.active ? 'Shutdown' : 'Activate')}
        onpointerdown={(e) => e.stopPropagation()}
      >{gen.active ? 'Shutdown Generator' : 'Activate Generator'}</button>
    </div>
  {/if}
</section>

<style>
  .gen {
    margin-top: 8px;
    padding-top: 7px;
    border-top: 1px solid var(--line);
  }

  .gen__head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    margin-bottom: 5px;
  }

  .gen__name {
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.22em;
    color: var(--fg-dim);
    text-transform: uppercase;
  }

  .gen__badge {
    padding: 1px 6px;
    font-family: var(--font-mono);
    font-size: 8px;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: var(--fg-mute);
    border: 1px solid var(--line);
  }
  .gen__badge[data-tone='active'] {
    color: var(--accent);
    border-color: var(--accent);
    background: rgba(126, 245, 184, 0.12);
    text-shadow: 0 0 4px var(--accent-glow);
  }
  .gen__badge[data-tone='warn'] {
    color: var(--warn);
    border-color: var(--warn);
    background: rgba(240, 180, 41, 0.1);
  }

  .gen__label {
    font-family: var(--font-mono);
    font-size: 8px;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: var(--fg-mute);
    margin-bottom: 1px;
  }
  .gen__label--inputs {
    margin-top: 4px;
  }

  .gen__row {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 6px;
    min-height: 12px;
    font-size: 9px;
    margin-bottom: 1px;
  }

  .gen__res-name {
    color: var(--fg);
    letter-spacing: 0.04em;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .gen__res-rate {
    font-family: var(--font-display);
    font-variant-numeric: tabular-nums;
  }
  .gen__res-rate--pos {
    color: var(--accent);
    text-shadow: 0 0 4px var(--accent-glow);
  }
  .gen__res-rate--neg {
    color: var(--warn);
  }
  .gen__res-rate em {
    font-style: normal;
    color: var(--fg-mute);
    font-size: 8px;
    letter-spacing: 0.08em;
    margin-left: 2px;
  }

  .gen__eff-row {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 6px;
    margin-top: 4px;
    font-size: 9px;
  }
  .gen__eff-label {
    color: var(--fg-dim);
    letter-spacing: 0.06em;
    text-transform: uppercase;
    font-size: 8px;
  }
  .gen__eff-value {
    font-family: var(--font-display);
    color: var(--accent);
    font-variant-numeric: tabular-nums;
    text-shadow: 0 0 4px var(--accent-glow);
  }
  .gen__eff-value em {
    font-style: normal;
    color: var(--fg-mute);
    font-size: 8px;
    letter-spacing: 0.08em;
    margin-left: 2px;
  }

  .gen__bar {
    position: relative;
    height: 4px;
    background: rgba(46, 106, 85, 0.2);
    border: 1px solid var(--line);
    margin: 1px 0 4px;
  }
  .gen__bar-fill {
    position: absolute;
    inset: 0 auto 0 0;
    width: var(--pct, 0%);
    background: var(--accent);
    box-shadow: 0 0 5px var(--accent-glow);
    transition: width 180ms ease;
  }

  .gen__status {
    font-family: var(--font-mono);
    font-size: 8px;
    color: var(--fg-dim);
    letter-spacing: 0.06em;
    margin-top: 2px;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .gen__events {
    display: flex;
    margin-top: 5px;
  }

  .gen__event {
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
    transition: background 160ms ease, border-color 160ms ease, color 160ms ease;
  }
  .gen__event:hover {
    background: rgba(126, 245, 184, 0.16);
    border-color: var(--accent);
    color: var(--accent-soft);
  }
</style>
