<script lang="ts">
  // Bespoke renderer for ModuleDeployableAntenna.
  //
  // Shares the 5-state deploy ladder with solar panels. We don't draw
  // sun-exposure readouts here — only the deploy state matters for
  // the animation + actionable button (Extend / Retract / Cannot
  // Retract on a non-retractable). If the same part has a sibling
  // ModuleDataTransmitter, that renderer draws next to this one and
  // handles the "can we actually transmit" question.
  //
  // Actions: invokeEvent('Extend' | 'Retract').

  import type { ModuleRendererProps } from './types';
  import type {
    PartModuleDeployableAntenna,
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
  const ant = $derived(module as PartModuleDeployableAntenna);
  const badge = $derived(STATE_BADGE[ant.state] ?? STATE_BADGE.retracted);
</script>

<section class="dant">
  <header class="dant__head">
    <span class="dant__name">DISH</span>
    <span class="dant__badge" data-tone={badge.tone}>{badge.label}</span>
  </header>

  <div class="dant__events">
    {#if ant.state === 'retracted' || ant.state === 'retracting'}
      <button
        type="button"
        class="dant__event"
        disabled={ant.state === 'retracting'}
        onclick={() => onInvokeEvent('Extend')}
        onpointerdown={(e) => e.stopPropagation()}
      >Deploy Dish</button>
    {:else if ant.state === 'extended' || ant.state === 'extending'}
      <button
        type="button"
        class="dant__event"
        disabled={!ant.retractable || ant.state === 'extending'}
        onclick={() => onInvokeEvent('Retract')}
        onpointerdown={(e) => e.stopPropagation()}
      >{ant.retractable ? 'Stow Dish' : 'Cannot Stow'}</button>
    {/if}
  </div>
</section>

<style>
  .dant {
    margin-top: 8px;
    padding-top: 7px;
    border-top: 1px solid var(--line);
  }
  .dant__head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    margin-bottom: 5px;
  }
  .dant__name {
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.22em;
    color: var(--fg-dim);
    text-transform: uppercase;
  }
  .dant__badge {
    padding: 1px 6px;
    font-family: var(--font-mono);
    font-size: 8px;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: var(--fg-mute);
    border: 1px solid var(--line);
  }
  .dant__badge[data-tone='active'] {
    color: var(--accent);
    border-color: var(--accent);
    background: rgba(126, 245, 184, 0.12);
    text-shadow: 0 0 3px var(--accent-glow);
  }
  .dant__badge[data-tone='busy'] {
    color: var(--info);
    border-color: var(--info);
    background: rgba(90, 176, 255, 0.1);
  }
  .dant__badge[data-tone='warn'] {
    color: var(--alert);
    border-color: var(--alert);
    background: rgba(255, 82, 82, 0.1);
  }

  .dant__events { display: flex; }
  .dant__event {
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
  .dant__event:hover:not(:disabled) {
    background: rgba(126, 245, 184, 0.16);
    border-color: var(--accent);
  }
  .dant__event:disabled { opacity: 0.4; cursor: default; }
</style>
