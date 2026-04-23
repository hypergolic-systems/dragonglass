<script lang="ts">
  // Bespoke renderer for ModuleDeployableSolarPanel.
  //
  // Three readouts people actually care about on a probe: current
  // output (EC/s), max possible (chargeRate), and sun angle of
  // attack (0..1). Hero shows current flow vs max as a filled bar;
  // below, a smaller sun-angle bar shows the exposure ratio so the
  // pilot can tell "panel occluded" from "panel aligned but far from
  // sun" at a glance.
  //
  // Renderer actions via invokeEvent:
  //   - state === 'retracted' → 'Extend'
  //   - state === 'extended' && retractable → 'Retract'
  //   - Broken panels get a warn pill, no actions.

  import type { ModuleRendererProps } from './types';
  import type {
    PartModuleSolarPanel,
    SolarPanelState,
  } from '@dragonglass/telemetry/core';

  const STATE_BADGE: Record<SolarPanelState, { label: string; tone: 'off' | 'active' | 'warn' | 'busy' }> = {
    retracted:  { label: 'Retracted',  tone: 'off' },
    extending:  { label: 'Extending…', tone: 'busy' },
    extended:   { label: 'Extended',   tone: 'active' },
    retracting: { label: 'Retracting…', tone: 'busy' },
    broken:     { label: 'Broken',     tone: 'warn' },
  };

  const { module, onInvokeEvent }: ModuleRendererProps = $props();
  const panel = $derived(module as PartModuleSolarPanel);
  const badge = $derived(STATE_BADGE[panel.state] ?? STATE_BADGE.retracted);

  // Output fraction vs maxrate — guards against a zero chargeRate
  // (some mod parts don't set one) so the bar doesn't render NaN.
  const outputPct = $derived.by(() => {
    if (panel.chargeRate <= 0) return 0;
    return Math.min(1, Math.max(0, panel.flowRate / panel.chargeRate)) * 100;
  });
  const aoaPct = $derived(Math.min(1, Math.max(0, panel.sunAOA)) * 100);

  const flowStr = $derived(fmt(panel.flowRate));
  const maxStr = $derived(fmt(panel.chargeRate));
  const aoaStr = $derived((panel.sunAOA * 100).toFixed(0));

  function fmt(v: number): string {
    if (v >= 100) return v.toFixed(0);
    if (v >= 10) return v.toFixed(1);
    return v.toFixed(2);
  }
</script>

<section class="solar">
  <header class="solar__head">
    <span class="solar__name">SOLAR</span>
    <span class="solar__badge" data-tone={badge.tone}>{badge.label}</span>
  </header>

  <!-- Output gauge: current EC/s flow vs max possible. -->
  <div class="solar__row">
    <span class="solar__label">Output</span>
    <span class="solar__value">
      {flowStr}<em>/ {maxStr} EC/s</em>
    </span>
  </div>
  <div class="solar__bar solar__bar--output" role="presentation">
    <div class="solar__bar-fill" style="--pct: {outputPct}%"></div>
  </div>

  <!-- Sun angle of attack — visible even when the panel is
       retracted, since occluded / shaded diagnostics matter before
       deploying. -->
  <div class="solar__row">
    <span class="solar__label">Sun AOA</span>
    <span class="solar__value">{aoaStr}<em>%</em></span>
  </div>
  <div class="solar__bar solar__bar--aoa" role="presentation">
    <div class="solar__bar-fill" style="--pct: {aoaPct}%"></div>
  </div>

  <div class="solar__events">
    {#if panel.state === 'retracted' || panel.state === 'retracting'}
      <button
        type="button"
        class="solar__event"
        disabled={panel.state === 'retracting'}
        onclick={() => onInvokeEvent('Extend')}
        onpointerdown={(e) => e.stopPropagation()}
      >Extend Panel</button>
    {:else if panel.state === 'extended' || panel.state === 'extending'}
      <button
        type="button"
        class="solar__event"
        disabled={!panel.retractable || panel.state === 'extending'}
        onclick={() => onInvokeEvent('Retract')}
        onpointerdown={(e) => e.stopPropagation()}
      >{panel.retractable ? 'Retract Panel' : 'Cannot Retract'}</button>
    {/if}
  </div>
</section>

<style>
  .solar {
    margin-top: 8px;
    padding-top: 7px;
    border-top: 1px solid var(--line);
  }

  .solar__head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    margin-bottom: 5px;
  }

  .solar__name {
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.22em;
    color: var(--fg-dim);
    text-transform: uppercase;
  }

  .solar__badge {
    padding: 1px 6px;
    font-family: var(--font-mono);
    font-size: 8px;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: var(--fg-mute);
    border: 1px solid var(--line);
  }
  .solar__badge[data-tone='active'] {
    color: var(--accent);
    border-color: var(--accent);
    background: rgba(126, 245, 184, 0.12);
    text-shadow: 0 0 4px var(--accent-glow);
  }
  .solar__badge[data-tone='busy'] {
    color: var(--info);
    border-color: var(--info);
    background: rgba(90, 176, 255, 0.12);
  }
  .solar__badge[data-tone='warn'] {
    color: var(--alert);
    border-color: var(--alert);
    background: rgba(255, 82, 82, 0.12);
  }

  .solar__row {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 6px;
    font-size: 9px;
    min-height: 13px;
  }

  .solar__label {
    color: var(--fg-dim);
    letter-spacing: 0.06em;
    text-transform: uppercase;
    font-size: 8px;
  }

  .solar__value {
    color: var(--accent);
    font-family: var(--font-display);
    font-variant-numeric: tabular-nums;
    text-shadow: 0 0 4px var(--accent-glow);
  }
  .solar__value em {
    font-style: normal;
    color: var(--fg-mute);
    font-size: 8px;
    letter-spacing: 0.08em;
    margin-left: 3px;
  }

  .solar__bar {
    position: relative;
    height: 4px;
    background: rgba(46, 106, 85, 0.2);
    border: 1px solid var(--line);
    margin: 1px 0 5px;
  }
  .solar__bar-fill {
    position: absolute;
    inset: 0 auto 0 0;
    width: var(--pct, 0%);
    background: var(--accent);
    box-shadow: 0 0 5px var(--accent-glow);
    transition: width 180ms ease;
  }
  /* AOA uses info-blue to visually distinguish the sun-angle bar
     from the output bar — they track different things at different
     rates, so a colour cue keeps the reading stack scannable. */
  .solar__bar--aoa .solar__bar-fill {
    background: var(--info);
    box-shadow: 0 0 4px var(--info-glow);
  }

  .solar__events {
    display: flex;
    margin-top: 2px;
  }

  .solar__event {
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
  .solar__event:hover:not(:disabled) {
    background: rgba(126, 245, 184, 0.16);
    border-color: var(--accent);
    color: var(--accent-soft);
  }
  .solar__event:disabled {
    opacity: 0.4;
    cursor: default;
  }
</style>
