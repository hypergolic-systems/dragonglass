<script lang="ts">
  // Bespoke renderer for ModuleRCS / ModuleRCSFX.
  //
  // Structure echoes the engine renderer — status badge, thrust-limit
  // slider, propellants — but RCS only has a 0..100 limiter, no
  // throttle. Isp and thrusterPower are the headline readouts.
  //
  // Actions:
  //   - setField('rcsEnabled', bool)
  //   - setField('thrustPercentage', 0..100)

  import type { ModuleRendererProps } from './types';
  import type { PartModuleRcs } from '@dragonglass/telemetry/core';

  const { module, onSetField }: ModuleRendererProps = $props();
  const rcs = $derived(module as PartModuleRcs);
  const limitPct = $derived(Math.max(0, Math.min(100, rcs.thrustLimit)));

  function seekLimit(e: MouseEvent) {
    const r = (e.currentTarget as HTMLElement).getBoundingClientRect();
    const pct = Math.max(0, Math.min(100, ((e.clientX - r.left) / r.width) * 100));
    onSetField('thrustPercentage', Math.round(pct));
  }
</script>

<section class="rcs">
  <header class="rcs__head">
    <span class="rcs__name">RCS</span>
    <button
      type="button"
      class="rcs__badge"
      data-tone={rcs.enabled ? 'active' : 'off'}
      onclick={() => onSetField('rcsEnabled', !rcs.enabled)}
      onpointerdown={(e) => e.stopPropagation()}
    >{rcs.enabled ? 'Enabled' : 'Disabled'}</button>
  </header>

  <div class="rcs__row">
    <span class="rcs__label">Thrust</span>
    <span class="rcs__value">
      {rcs.thrusterPower.toFixed(1)}<em>kN</em>
    </span>
  </div>
  <div class="rcs__row">
    <span class="rcs__label">ISP</span>
    <span class="rcs__value">{rcs.realIsp.toFixed(0)}<em>s</em></span>
  </div>

  <div class="rcs__row">
    <span class="rcs__label">Limit</span>
    <span class="rcs__value">{limitPct.toFixed(0)}<em>%</em></span>
  </div>
  <!-- svelte-ignore a11y_click_events_have_key_events -->
  <!-- svelte-ignore a11y_no_static_element_interactions -->
  <div class="rcs__bar" onclick={seekLimit} onpointerdown={(e) => e.stopPropagation()}>
    <div class="rcs__bar-fill" style="--pct: {limitPct}%"></div>
  </div>

  {#if rcs.propellants.length > 0}
    <div class="rcs__prop-label">Propellant</div>
    {#each rcs.propellants as p (p.name)}
      <div class="rcs__prop">
        <span class="rcs__prop-name">{p.displayName || p.name}</span>
        <span class="rcs__prop-amt">
          {p.totalAvailable.toFixed(0)}<em>u</em>
        </span>
      </div>
    {/each}
  {/if}
</section>

<style>
  .rcs {
    margin-top: 8px;
    padding-top: 7px;
    border-top: 1px solid var(--line);
  }
  .rcs__head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    margin-bottom: 5px;
  }
  .rcs__name {
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.22em;
    color: var(--fg-dim);
    text-transform: uppercase;
  }
  .rcs__badge {
    padding: 1px 6px;
    font-family: var(--font-mono);
    font-size: 8px;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: var(--fg-mute);
    border: 1px solid var(--line);
    background: transparent;
    cursor: pointer;
  }
  .rcs__badge[data-tone='active'] {
    color: var(--accent);
    border-color: var(--accent);
    background: rgba(126, 245, 184, 0.12);
    text-shadow: 0 0 3px var(--accent-glow);
  }

  .rcs__row {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 6px;
    font-size: 9px;
    min-height: 12px;
    margin-bottom: 1px;
  }
  .rcs__label {
    color: var(--fg-dim);
    letter-spacing: 0.06em;
    text-transform: uppercase;
    font-size: 8px;
  }
  .rcs__value {
    color: var(--accent);
    font-family: var(--font-display);
    font-variant-numeric: tabular-nums;
    text-shadow: 0 0 4px var(--accent-glow);
  }
  .rcs__value em {
    font-style: normal;
    color: var(--fg-mute);
    font-size: 8px;
    letter-spacing: 0.08em;
    margin-left: 3px;
  }

  .rcs__bar {
    position: relative;
    height: 4px;
    background: rgba(46, 106, 85, 0.2);
    border: 1px solid var(--line);
    margin: 1px 0 5px;
    cursor: pointer;
  }
  .rcs__bar-fill {
    position: absolute;
    inset: 0 auto 0 0;
    width: var(--pct, 0%);
    background: var(--accent);
    box-shadow: 0 0 5px var(--accent-glow);
    transition: width 140ms ease;
  }

  .rcs__prop-label {
    font-family: var(--font-mono);
    font-size: 8px;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: var(--fg-mute);
    margin: 3px 0 1px;
  }
  .rcs__prop {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 6px;
    font-size: 9px;
    min-height: 11px;
  }
  .rcs__prop-name {
    color: var(--fg);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .rcs__prop-amt {
    color: var(--accent);
    font-family: var(--font-display);
    font-variant-numeric: tabular-nums;
  }
  .rcs__prop-amt em {
    font-style: normal;
    color: var(--fg-mute);
    font-size: 8px;
    margin-left: 2px;
  }
</style>
