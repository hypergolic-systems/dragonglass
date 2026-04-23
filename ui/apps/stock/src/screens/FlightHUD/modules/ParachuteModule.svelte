<script lang="ts">
  // Bespoke renderer for ModuleParachute.
  //
  // Hero: a 4-stage deployment ladder (STOWED → ACTIVE → SEMI →
  // FULL) with the current stage highlighted in phosphor. CUT is
  // rendered as a cross-out over DEPLOYED.
  //
  // Below the ladder, a "safe / risky / unsafe" indicator mirrors
  // stock's dynamic-pressure safety readout so the pilot knows
  // whether hitting Deploy right now would shred the canopy.
  //
  // Actions via invokeEvent:
  //   - stowed            → 'Deploy'
  //   - active/semi/full  → 'CutParachute'
  //   - cut               → 'Repack' (only works on-ground / by EVA)

  import type { ModuleRendererProps } from './types';
  import type {
    PartModuleParachute,
    ParachuteState,
    ParachuteSafeState,
  } from '@dragonglass/telemetry/core';

  const LADDER: ParachuteState[] = ['stowed', 'active', 'semi', 'deployed'];
  const LADDER_LABELS: Record<ParachuteState, string> = {
    stowed: 'Stowed',
    active: 'Armed',
    semi: 'Semi',
    deployed: 'Full',
    cut: 'Cut',
  };
  const SAFE_TONE: Record<ParachuteSafeState, 'off' | 'active' | 'warn' | 'alert'> = {
    none: 'off',
    safe: 'active',
    risky: 'warn',
    unsafe: 'alert',
  };
  const SAFE_LABEL: Record<ParachuteSafeState, string> = {
    none: '—',
    safe: 'Safe',
    risky: 'Risky',
    unsafe: 'Unsafe',
  };

  const { module, onInvokeEvent }: ModuleRendererProps = $props();
  const chute = $derived(module as PartModuleParachute);
  const activeIdx = $derived(LADDER.indexOf(chute.state));
  const safeTone = $derived(SAFE_TONE[chute.safeState] ?? 'off');
  const safeLabel = $derived(SAFE_LABEL[chute.safeState] ?? '—');
</script>

<section class="chute" class:chute--cut={chute.state === 'cut'}>
  <header class="chute__head">
    <span class="chute__name">PARACHUTE</span>
    <span class="chute__safe" data-tone={safeTone}>{safeLabel}</span>
  </header>

  <!-- Deployment ladder. Segments before the current stage glow
       phosphor; the current stage pulses; later stages stay dim.
       A 'cut' state greys the whole track out and adds a strike. -->
  <ol class="chute__ladder">
    {#each LADDER as step, i (step)}
      <li
        class="chute__step"
        class:chute__step--active={i === activeIdx && chute.state !== 'cut'}
        class:chute__step--past={i < activeIdx && chute.state !== 'cut'}
      >
        <span class="chute__step-bar" aria-hidden="true"></span>
        <span class="chute__step-label">{LADDER_LABELS[step]}</span>
      </li>
    {/each}
  </ol>

  <div class="chute__row">
    <span class="chute__label">Deploy alt</span>
    <span class="chute__value">
      {chute.deployAltitude.toFixed(0)}<em>m</em>
    </span>
  </div>

  <div class="chute__events">
    {#if chute.state === 'stowed'}
      <button
        type="button"
        class="chute__event chute__event--warn"
        onclick={() => onInvokeEvent('Deploy')}
        onpointerdown={(e) => e.stopPropagation()}
      >Deploy</button>
    {:else if chute.state === 'cut'}
      <button
        type="button"
        class="chute__event"
        onclick={() => onInvokeEvent('Repack')}
        onpointerdown={(e) => e.stopPropagation()}
      >Repack</button>
    {:else}
      <button
        type="button"
        class="chute__event chute__event--alert"
        onclick={() => onInvokeEvent('CutParachute')}
        onpointerdown={(e) => e.stopPropagation()}
      >Cut Chute</button>
    {/if}
  </div>
</section>

<style>
  .chute {
    margin-top: 8px;
    padding-top: 7px;
    border-top: 1px solid var(--line);
  }

  .chute__head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    margin-bottom: 5px;
  }

  .chute__name {
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.22em;
    color: var(--fg-dim);
    text-transform: uppercase;
  }

  .chute__safe {
    padding: 1px 6px;
    font-family: var(--font-mono);
    font-size: 8px;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: var(--fg-mute);
    border: 1px solid var(--line);
  }
  .chute__safe[data-tone='active'] {
    color: var(--accent);
    border-color: var(--accent);
    background: rgba(126, 245, 184, 0.12);
    text-shadow: 0 0 4px var(--accent-glow);
  }
  .chute__safe[data-tone='warn'] {
    color: var(--warn);
    border-color: var(--warn);
    background: rgba(240, 180, 41, 0.12);
  }
  .chute__safe[data-tone='alert'] {
    color: var(--alert);
    border-color: var(--alert);
    background: rgba(255, 82, 82, 0.12);
  }

  /* Deployment ladder: 4 equal cells with a thin bar under each
     label. Past cells + current cell light up; the current pulses.
     A cut state strikes the whole track through. */
  .chute__ladder {
    list-style: none;
    margin: 0 0 5px;
    padding: 0;
    display: grid;
    grid-template-columns: repeat(4, 1fr);
    gap: 3px;
    position: relative;
  }

  .chute__step {
    display: flex;
    flex-direction: column;
    align-items: stretch;
    gap: 2px;
  }

  .chute__step-bar {
    display: block;
    height: 3px;
    background: var(--line-bright);
    opacity: 0.4;
    transition: background 200ms ease, opacity 200ms ease, box-shadow 200ms ease;
  }

  .chute__step-label {
    font-family: var(--font-mono);
    font-size: 7px;
    letter-spacing: 0.12em;
    text-transform: uppercase;
    color: var(--fg-mute);
    text-align: center;
    transition: color 200ms ease;
  }

  .chute__step--past .chute__step-bar {
    background: var(--accent-dim);
    opacity: 0.7;
  }
  .chute__step--past .chute__step-label {
    color: var(--fg-dim);
  }

  .chute__step--active .chute__step-bar {
    background: var(--accent);
    opacity: 1;
    box-shadow: 0 0 6px var(--accent-glow);
  }
  .chute__step--active .chute__step-label {
    color: var(--accent);
    text-shadow: 0 0 4px var(--accent-glow);
  }

  .chute--cut .chute__ladder::after {
    content: '';
    position: absolute;
    left: 0;
    right: 0;
    top: 1px;
    height: 3px;
    background: var(--alert);
    opacity: 0.8;
    box-shadow: 0 0 4px rgba(255, 82, 82, 0.5);
  }

  .chute__row {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 6px;
    min-height: 13px;
    font-size: 9px;
    margin-bottom: 4px;
  }

  .chute__label {
    color: var(--fg-dim);
    letter-spacing: 0.06em;
    text-transform: uppercase;
    font-size: 8px;
  }

  .chute__value {
    color: var(--accent);
    font-family: var(--font-display);
    font-variant-numeric: tabular-nums;
    text-shadow: 0 0 4px var(--accent-glow);
  }
  .chute__value em {
    font-style: normal;
    color: var(--fg-mute);
    font-size: 8px;
    letter-spacing: 0.08em;
    margin-left: 2px;
  }

  .chute__events {
    display: flex;
  }

  .chute__event {
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
  .chute__event:hover {
    background: rgba(126, 245, 184, 0.16);
    border-color: var(--accent);
    color: var(--accent-soft);
  }
  /* Stowed → Deploy: warn amber, because deploying at the wrong
     moment kills the chute. Pilot's eye lingers before clicking. */
  .chute__event--warn {
    color: var(--warn);
    background: rgba(240, 180, 41, 0.08);
    border-color: var(--warn);
  }
  .chute__event--warn:hover {
    background: rgba(240, 180, 41, 0.18);
    color: var(--fg);
  }
  /* Cut Chute: alert red, always destructive. */
  .chute__event--alert {
    color: var(--alert);
    background: rgba(255, 82, 82, 0.08);
    border-color: var(--alert);
  }
  .chute__event--alert:hover {
    background: rgba(255, 82, 82, 0.18);
    color: var(--fg);
  }
</style>
