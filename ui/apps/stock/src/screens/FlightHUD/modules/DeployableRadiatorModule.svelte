<script lang="ts">
  // Bespoke renderer for ModuleDeployableRadiator.
  //
  // Cousin of the deployable antenna — same 5-state ladder, different
  // labels + button copy. If the part has a sibling
  // ModuleActiveRadiator, its renderer draws below this one with the
  // heat-flux readout.
  //
  // Actions: invokeEvent('Extend' | 'Retract').

  import type { ModuleRendererProps } from './types';
  import type {
    PartModuleDeployableRadiator,
    SolarPanelState,
  } from '@dragonglass/telemetry/core';

  const STATE_BADGE: Record<SolarPanelState, { label: string; tone: 'off' | 'active' | 'warn' | 'busy' }> = {
    retracted:  { label: 'Stowed',     tone: 'off' },
    extending:  { label: 'Deploying…', tone: 'busy' },
    extended:   { label: 'Deployed',   tone: 'active' },
    retracting: { label: 'Stowing…',   tone: 'busy' },
    broken:     { label: 'Broken',     tone: 'warn' },
  };

  const { module, onInvokeEvent }: ModuleRendererProps = $props();
  const rad = $derived(module as PartModuleDeployableRadiator);
  const badge = $derived(STATE_BADGE[rad.state] ?? STATE_BADGE.retracted);
</script>

<section class="drad">
  <header class="drad__head">
    <span class="drad__name">RADIATOR</span>
    <span class="drad__badge" data-tone={badge.tone}>{badge.label}</span>
  </header>

  <div class="drad__events">
    {#if rad.state === 'retracted' || rad.state === 'retracting'}
      <button
        type="button"
        class="drad__event"
        disabled={rad.state === 'retracting'}
        onclick={() => onInvokeEvent('Extend')}
        onpointerdown={(e) => e.stopPropagation()}
      >Deploy Radiator</button>
    {:else if rad.state === 'extended' || rad.state === 'extending'}
      <button
        type="button"
        class="drad__event"
        disabled={!rad.retractable || rad.state === 'extending'}
        onclick={() => onInvokeEvent('Retract')}
        onpointerdown={(e) => e.stopPropagation()}
      >{rad.retractable ? 'Stow Radiator' : 'Cannot Stow'}</button>
    {/if}
  </div>
</section>

<style>
  .drad {
    margin-top: 8px;
    padding-top: 7px;
    border-top: 1px solid var(--line);
  }
  .drad__head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    margin-bottom: 5px;
  }
  .drad__name {
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.22em;
    color: var(--fg-dim);
    text-transform: uppercase;
  }
  .drad__badge {
    padding: 1px 6px;
    font-family: var(--font-mono);
    font-size: 8px;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: var(--fg-mute);
    border: 1px solid var(--line);
  }
  .drad__badge[data-tone='active'] {
    color: var(--accent);
    border-color: var(--accent);
    background: rgba(126, 245, 184, 0.12);
    text-shadow: 0 0 3px var(--accent-glow);
  }
  .drad__badge[data-tone='busy'] {
    color: var(--info);
    border-color: var(--info);
    background: rgba(90, 176, 255, 0.1);
  }
  .drad__badge[data-tone='warn'] {
    color: var(--alert);
    border-color: var(--alert);
    background: rgba(255, 82, 82, 0.1);
  }

  .drad__events { display: flex; }
  .drad__event {
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
  .drad__event:hover:not(:disabled) {
    background: rgba(126, 245, 184, 0.16);
    border-color: var(--accent);
  }
  .drad__event:disabled { opacity: 0.4; cursor: default; }
</style>
