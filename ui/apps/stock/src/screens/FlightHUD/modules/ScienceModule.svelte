<script lang="ts">
  // Bespoke renderer for ModuleScienceExperiment.
  //
  // Stock's PAW for a science experiment is the most-interacted UI in
  // the game for mission pilots: Run → Review → Transmit → Reset.
  // This renderer mirrors that loop as a mission console:
  //
  //   ┌─ SCIENCE ────────────── READY ┐
  //   │ Temperature Scan               │   ← experiment title
  //   │                                │
  //   │        3.20              0.8   │   ← hero: transmit value (pts) / data (Mits)
  //   │   TRANSMIT VALUE       DATA    │
  //   │                                │
  //   │  [ REVIEW DATA ] [ RESET ]     │   ← state-specific action cluster
  //   └────────────────────────────────┘
  //
  // Actions map to stock KSPEvents by name:
  //   - state === 'stowed'     → DeployExperiment
  //   - state === 'ready'      → ReviewDataEvent + ResetExperiment
  //   - state === 'inoperable' → no actions (badge explains)
  //
  // "Review Data" surfaces stock's own ExperimentsResultDialog
  // (Keep/Discard/Transmit/Lab) — we don't reimplement that modal,
  // just trigger it. The rest of the lifecycle stays in the bespoke.

  import type { ModuleRendererProps } from './types';
  import type {
    PartModuleScienceExperiment,
    ScienceExperimentState,
  } from '@dragonglass/telemetry/core';

  const STATE_BADGE_TONE: Record<ScienceExperimentState, 'off' | 'active' | 'warn'> = {
    stowed: 'off',
    ready: 'active',
    inoperable: 'warn',
  };

  const STATE_BADGE_LABEL: Record<ScienceExperimentState, string> = {
    stowed: 'Ready',
    ready: 'Data',
    inoperable: 'Spent',
  };

  const { module, onInvokeEvent }: ModuleRendererProps = $props();
  const sci = $derived(module as PartModuleScienceExperiment);
  const tone = $derived(STATE_BADGE_TONE[sci.state]);
  const badge = $derived(STATE_BADGE_LABEL[sci.state]);
  const hasData = $derived(sci.state === 'ready' && sci.dataAmount > 0);
</script>

<section class="sci">
  <header class="sci__head">
    <span class="sci__name">SCIENCE</span>
    <span class="sci__badge" data-tone={tone}>{badge}</span>
  </header>
  <div class="sci__title">{sci.experimentTitle || 'Experiment'}</div>

  <div class="sci__hero">
    <div class="sci__stat">
      <span class="sci__value">{sci.transmitValue.toFixed(2)}</span>
      <span class="sci__stat-label">Transmit · science</span>
    </div>
    <div class="sci__divider" aria-hidden="true"></div>
    <div class="sci__stat">
      <span class="sci__value sci__value--mits">{sci.dataAmount.toFixed(1)}</span>
      <span class="sci__stat-label">Data · Mits</span>
    </div>
  </div>

  <div class="sci__events">
    {#if sci.state === 'stowed'}
      <button
        type="button"
        class="sci__event sci__event--primary"
        onclick={() => onInvokeEvent('DeployExperiment')}
        onpointerdown={(e) => e.stopPropagation()}
      >Run Experiment</button>
    {:else if sci.state === 'ready'}
      <button
        type="button"
        class="sci__event sci__event--primary"
        disabled={!hasData}
        onclick={() => onInvokeEvent('ReviewDataEvent')}
        onpointerdown={(e) => e.stopPropagation()}
      >Review Data</button>
      <button
        type="button"
        class="sci__event"
        onclick={() => onInvokeEvent('ResetExperiment')}
        onpointerdown={(e) => e.stopPropagation()}
      >Reset</button>
    {:else}
      <span class="sci__note">
        {sci.rerunnable ? 'Awaiting reset' : 'Experiment consumed — part is inoperable.'}
      </span>
    {/if}
  </div>
</section>

<style>
  .sci {
    margin-top: 8px;
    padding-top: 7px;
    border-top: 1px solid var(--line);
  }

  .sci__head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    margin-bottom: 3px;
  }

  .sci__name {
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.22em;
    color: var(--fg-dim);
    text-transform: uppercase;
  }

  .sci__badge {
    padding: 1px 6px;
    font-family: var(--font-mono);
    font-size: 8px;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: var(--fg-mute);
    border: 1px solid var(--line);
  }
  .sci__badge[data-tone='active'] {
    color: var(--accent);
    border-color: var(--accent);
    background: rgba(126, 245, 184, 0.12);
    text-shadow: 0 0 4px var(--accent-glow);
  }
  .sci__badge[data-tone='warn'] {
    color: var(--warn);
    border-color: var(--warn);
    background: rgba(240, 180, 41, 0.1);
  }

  .sci__title {
    font-family: var(--font-display);
    font-size: 11px;
    letter-spacing: 0.08em;
    text-transform: uppercase;
    color: var(--fg);
    margin-bottom: 6px;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  /* Paired hero — two stat blocks side by side separated by a thin
     divider. Transmit value is the primary-colour one because it's
     the player's headline gain; data amount is secondary. */
  .sci__hero {
    display: flex;
    align-items: stretch;
    gap: 8px;
    padding: 6px 4px 4px;
    background: rgba(126, 245, 184, 0.03);
    border: 1px solid var(--line-accent);
    box-shadow: inset 0 0 8px rgba(126, 245, 184, 0.05);
    margin-bottom: 6px;
  }

  .sci__stat {
    flex: 1 1 0;
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 1px;
    min-width: 0;
  }

  .sci__value {
    font-family: var(--font-display);
    font-size: 18px;
    line-height: 1;
    color: var(--accent);
    font-variant-numeric: tabular-nums;
    text-shadow: 0 0 8px var(--accent-glow);
  }
  .sci__value--mits {
    color: var(--info);
    text-shadow: 0 0 6px var(--info-glow);
  }

  .sci__stat-label {
    font-family: var(--font-mono);
    font-size: 7px;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: var(--fg-mute);
  }

  .sci__divider {
    width: 1px;
    background: var(--line-bright);
    opacity: 0.4;
  }

  /* Actions — one "primary" button stretches, secondary buttons sit
     compact beside it. Disabled state fades without losing outline. */
  .sci__events {
    display: flex;
    gap: 4px;
  }

  .sci__event {
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
  .sci__event--primary {
    flex: 1 1 auto;
  }
  .sci__event:hover:not(:disabled) {
    background: rgba(126, 245, 184, 0.16);
    border-color: var(--accent);
    color: var(--accent-soft);
  }
  .sci__event:disabled {
    opacity: 0.4;
    cursor: default;
  }

  .sci__note {
    flex: 1 1 auto;
    font-family: var(--font-mono);
    font-size: 9px;
    letter-spacing: 0.06em;
    color: var(--fg-mute);
    padding: 4px 6px;
    border: 1px dashed var(--line);
  }
</style>
