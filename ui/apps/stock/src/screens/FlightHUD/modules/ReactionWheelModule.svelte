<script lang="ts">
  // Bespoke renderer for ModuleReactionWheel.
  //
  // The headline is the P/Y/R torque triplet and the authority
  // limiter slider. State is Active / Disabled / Broken; actuator mode
  // cycles between Normal / SAS-only / Pilot-only so the renderer
  // mirrors stock's UI_Cycle.
  //
  // Actions:
  //   - invokeEvent('OnToggle')        — activate / disable
  //   - setField('authorityLimiter', 0..100)
  //   - setField('actuatorModeCycle', 0|1|2)

  import type { ModuleRendererProps } from './types';
  import type {
    PartModuleReactionWheel,
    ReactionWheelState,
  } from '@dragonglass/telemetry/core';

  const STATE_TONE: Record<ReactionWheelState, 'off' | 'active' | 'alert'> = {
    active: 'active',
    disabled: 'off',
    broken: 'alert',
  };
  const STATE_LABEL: Record<ReactionWheelState, string> = {
    active: 'Active',
    disabled: 'Disabled',
    broken: 'Broken',
  };
  const MODES = ['Normal', 'SAS', 'Pilot'] as const;

  const { module, onInvokeEvent, onSetField }: ModuleRendererProps = $props();
  const rw = $derived(module as PartModuleReactionWheel);
  const tone = $derived(STATE_TONE[rw.state]);
  const label = $derived(STATE_LABEL[rw.state]);
  const modeName = $derived(MODES[rw.actuatorMode] ?? 'Normal');
  const authorityPct = $derived(Math.max(0, Math.min(100, rw.authorityLimiter)));

  function cycleMode() {
    const next = (rw.actuatorMode + 1) % MODES.length;
    onSetField('actuatorModeCycle', next);
  }
</script>

<section class="rw">
  <header class="rw__head">
    <span class="rw__name">REACTION WHEEL</span>
    <span class="rw__badge" data-tone={tone}>{label}</span>
  </header>

  <!-- P/Y/R torque triplet. Three stat blocks share the same row
       height; kN·m is the stock unit. Labels below so the eye lands on
       the numbers first. -->
  <div class="rw__torque">
    <div class="rw__stat">
      <span class="rw__num">{rw.pitchTorque.toFixed(1)}</span>
      <span class="rw__stat-label">Pitch</span>
    </div>
    <div class="rw__stat">
      <span class="rw__num">{rw.yawTorque.toFixed(1)}</span>
      <span class="rw__stat-label">Yaw</span>
    </div>
    <div class="rw__stat">
      <span class="rw__num">{rw.rollTorque.toFixed(1)}</span>
      <span class="rw__stat-label">Roll</span>
    </div>
  </div>

  <div class="rw__row">
    <span class="rw__label">Authority</span>
    <span class="rw__value">{authorityPct.toFixed(0)}<em>%</em></span>
  </div>
  <!-- Authority limiter drives the stock 0..100 float. Clicking on
       the bar seeks to that fraction. -->
  <!-- svelte-ignore a11y_click_events_have_key_events -->
  <!-- svelte-ignore a11y_no_static_element_interactions -->
  <div
    class="rw__bar"
    onclick={(e) => {
      const r = (e.currentTarget as HTMLElement).getBoundingClientRect();
      const pct = Math.max(0, Math.min(100, ((e.clientX - r.left) / r.width) * 100));
      onSetField('authorityLimiter', pct);
    }}
    onpointerdown={(e) => e.stopPropagation()}
  >
    <div class="rw__bar-fill" style="--pct: {authorityPct}%"></div>
  </div>

  <div class="rw__row rw__row--mode">
    <span class="rw__label">Mode</span>
    <button
      type="button"
      class="rw__mode"
      onclick={cycleMode}
      onpointerdown={(e) => e.stopPropagation()}
    >{modeName}</button>
  </div>

  <div class="rw__events">
    <button
      type="button"
      class="rw__event"
      onclick={() => onInvokeEvent('OnToggle')}
      onpointerdown={(e) => e.stopPropagation()}
    >{rw.state === 'active' ? 'Disable' : 'Activate'}</button>
  </div>
</section>

<style>
  .rw {
    margin-top: 8px;
    padding-top: 7px;
    border-top: 1px solid var(--line);
  }

  .rw__head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    margin-bottom: 5px;
  }

  .rw__name {
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.22em;
    color: var(--fg-dim);
    text-transform: uppercase;
  }

  .rw__badge {
    padding: 1px 6px;
    font-family: var(--font-mono);
    font-size: 8px;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: var(--fg-mute);
    border: 1px solid var(--line);
  }
  .rw__badge[data-tone='active'] {
    color: var(--accent);
    border-color: var(--accent);
    background: rgba(126, 245, 184, 0.12);
  }
  .rw__badge[data-tone='alert'] {
    color: var(--alert);
    border-color: var(--alert);
    background: rgba(255, 82, 82, 0.1);
  }

  .rw__torque {
    display: grid;
    grid-template-columns: repeat(3, 1fr);
    gap: 4px;
    padding: 5px 2px 4px;
    margin-bottom: 4px;
    border: 1px solid var(--line-accent);
    background: rgba(126, 245, 184, 0.03);
  }
  .rw__stat {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 2px;
  }
  .rw__num {
    font-family: var(--font-display);
    font-size: 14px;
    line-height: 1;
    color: var(--accent);
    font-variant-numeric: tabular-nums;
    text-shadow: 0 0 6px var(--accent-glow);
  }
  .rw__stat-label {
    font-family: var(--font-mono);
    font-size: 7px;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: var(--fg-mute);
  }

  .rw__row {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 6px;
    font-size: 9px;
    min-height: 12px;
  }
  .rw__row--mode {
    align-items: center;
    margin-top: 4px;
  }

  .rw__label {
    color: var(--fg-dim);
    letter-spacing: 0.06em;
    text-transform: uppercase;
    font-size: 8px;
  }

  .rw__value {
    color: var(--accent);
    font-family: var(--font-display);
    font-variant-numeric: tabular-nums;
    text-shadow: 0 0 4px var(--accent-glow);
  }
  .rw__value em {
    font-style: normal;
    color: var(--fg-mute);
    font-size: 8px;
    letter-spacing: 0.08em;
    margin-left: 3px;
  }

  .rw__bar {
    position: relative;
    height: 4px;
    background: rgba(46, 106, 85, 0.2);
    border: 1px solid var(--line);
    margin: 1px 0 5px;
    cursor: pointer;
  }
  .rw__bar-fill {
    position: absolute;
    inset: 0 auto 0 0;
    width: var(--pct, 0%);
    background: var(--accent);
    box-shadow: 0 0 5px var(--accent-glow);
    transition: width 140ms ease;
  }

  .rw__mode {
    min-width: 58px;
    padding: 1px 6px;
    font-family: var(--font-mono);
    font-size: 8px;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: var(--accent);
    border: 1px solid var(--line-accent);
    background: transparent;
    cursor: pointer;
  }
  .rw__mode:hover {
    background: rgba(126, 245, 184, 0.12);
  }

  .rw__events {
    display: flex;
    margin-top: 5px;
  }
  .rw__event {
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
  .rw__event:hover {
    background: rgba(126, 245, 184, 0.16);
    border-color: var(--accent);
  }
</style>
