<script lang="ts">
  // Bespoke renderer for ModuleCommand — crewed pods and probe cores.
  //
  // Stock's command PAW is mostly about (a) who's in the seat and can
  // they fly the ship, (b) toggling hibernation, and (c) "Control from
  // here" for multi-cockpit vessels. This renderer mirrors that,
  // skipping the cosmetic rename + control-point buttons that most
  // players touch once a mission.
  //
  // Actions:
  //   - invokeEvent('MakeReference')   — "Control From Here"
  //   - invokeEvent('RenameVessel')    — rename dialog
  //   - setField('hibernate', bool)    — toggle hibernation
  //   - setField('hibernateOnWarp', bool)

  import type { ModuleRendererProps } from './types';
  import type {
    PartModuleCommand,
    CommandControlState,
  } from '@dragonglass/telemetry/core';

  const STATE_TONE: Record<CommandControlState, 'off' | 'active' | 'warn' | 'alert' | 'info'> = {
    nominal:     'active',
    partial:     'warn',
    uncrewed:    'alert',
    hibernating: 'info',
    nosignal:    'alert',
  };
  const STATE_LABEL: Record<CommandControlState, string> = {
    nominal:     'Control',
    partial:     'Partial',
    uncrewed:    'Uncrewed',
    hibernating: 'Hibernate',
    nosignal:    'No Signal',
  };

  const { module, onInvokeEvent, onSetField }: ModuleRendererProps = $props();
  const cmd = $derived(module as PartModuleCommand);
  const tone = $derived(STATE_TONE[cmd.controlState] ?? 'off');
  const label = $derived(STATE_LABEL[cmd.controlState] ?? '—');
  const crewReq = $derived(cmd.minimumCrew > 0);
</script>

<section class="cmd">
  <header class="cmd__head">
    <span class="cmd__name">COMMAND</span>
    <span class="cmd__badge" data-tone={tone}>{label}</span>
  </header>

  <div class="cmd__row">
    <span class="cmd__label">Crew</span>
    <span class="cmd__value">
      {cmd.crewCount}{#if crewReq}<em>/ {cmd.minimumCrew} req</em>{/if}
    </span>
  </div>

  <!-- Hibernation toggles. Stock treats these as KSPFields on the
       module, so they round-trip via setField; the UI_Toggle labels
       are "Enabled"/"Disabled" in stock, which reads backward here
       (hibernate=true = drain low). We re-label to "On/Off". -->
  <div class="cmd__row cmd__row--toggle">
    <span class="cmd__label">Hibernate</span>
    <button
      type="button"
      class="cmd__toggle"
      class:cmd__toggle--on={cmd.hibernate}
      onclick={() => onSetField('hibernate', !cmd.hibernate)}
      onpointerdown={(e) => e.stopPropagation()}
    >{cmd.hibernate ? 'On' : 'Off'}</button>
  </div>
  <div class="cmd__row cmd__row--toggle">
    <span class="cmd__label">… on warp</span>
    <button
      type="button"
      class="cmd__toggle"
      class:cmd__toggle--on={cmd.hibernateOnWarp}
      onclick={() => onSetField('hibernateOnWarp', !cmd.hibernateOnWarp)}
      onpointerdown={(e) => e.stopPropagation()}
    >{cmd.hibernateOnWarp ? 'On' : 'Off'}</button>
  </div>

  <div class="cmd__events">
    <button
      type="button"
      class="cmd__event"
      onclick={() => onInvokeEvent('MakeReference')}
      onpointerdown={(e) => e.stopPropagation()}
    >Control Here</button>
    <button
      type="button"
      class="cmd__event"
      onclick={() => onInvokeEvent('RenameVessel')}
      onpointerdown={(e) => e.stopPropagation()}
    >Rename</button>
  </div>
</section>

<style>
  .cmd {
    margin-top: 8px;
    padding-top: 7px;
    border-top: 1px solid var(--line);
  }

  .cmd__head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    margin-bottom: 5px;
  }

  .cmd__name {
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.22em;
    color: var(--fg-dim);
    text-transform: uppercase;
  }

  .cmd__badge {
    padding: 1px 6px;
    font-family: var(--font-mono);
    font-size: 8px;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: var(--fg-mute);
    border: 1px solid var(--line);
  }
  .cmd__badge[data-tone='active'] {
    color: var(--accent);
    border-color: var(--accent);
    background: rgba(126, 245, 184, 0.12);
    text-shadow: 0 0 4px var(--accent-glow);
  }
  .cmd__badge[data-tone='warn'] {
    color: var(--warn);
    border-color: var(--warn);
    background: rgba(240, 180, 41, 0.1);
  }
  .cmd__badge[data-tone='alert'] {
    color: var(--alert);
    border-color: var(--alert);
    background: rgba(255, 82, 82, 0.1);
  }
  .cmd__badge[data-tone='info'] {
    color: var(--info);
    border-color: var(--info);
    background: rgba(90, 176, 255, 0.1);
  }

  .cmd__row {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 6px;
    min-height: 13px;
    font-size: 9px;
    margin-bottom: 2px;
  }
  .cmd__row--toggle {
    align-items: center;
  }

  .cmd__label {
    color: var(--fg-dim);
    letter-spacing: 0.06em;
    text-transform: uppercase;
    font-size: 8px;
  }

  .cmd__value {
    color: var(--accent);
    font-family: var(--font-display);
    font-variant-numeric: tabular-nums;
    text-shadow: 0 0 4px var(--accent-glow);
  }
  .cmd__value em {
    font-style: normal;
    color: var(--fg-mute);
    font-size: 8px;
    letter-spacing: 0.06em;
    margin-left: 3px;
  }

  .cmd__toggle {
    min-width: 38px;
    padding: 1px 6px;
    font-family: var(--font-mono);
    font-size: 8px;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: var(--fg-mute);
    background: transparent;
    border: 1px solid var(--line);
    cursor: pointer;
  }
  .cmd__toggle--on {
    color: var(--accent);
    border-color: var(--accent);
    background: rgba(126, 245, 184, 0.12);
    text-shadow: 0 0 3px var(--accent-glow);
  }

  .cmd__events {
    display: flex;
    gap: 4px;
    margin-top: 5px;
  }

  .cmd__event {
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
  .cmd__event:hover {
    background: rgba(126, 245, 184, 0.16);
    border-color: var(--accent);
    color: var(--accent-soft);
  }
</style>
