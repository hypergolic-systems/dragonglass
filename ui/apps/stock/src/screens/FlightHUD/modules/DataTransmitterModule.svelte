<script lang="ts">
  // Bespoke renderer for ModuleDataTransmitter.
  //
  // Player-facing essentials:
  //   - Antenna type (direct / relay / internal) as a one-word badge.
  //   - Range (antennaPower) in SI-formatted metres.
  //   - Packet size × interval — a "1 mits every 0.5s" throughput read.
  //   - Start/Stop transmission button when busy.
  //
  // Actions: invokeEvent('StartTransmission' | 'StopTransmission').

  import type { ModuleRendererProps } from './types';
  import type {
    PartModuleDataTransmitter,
    AntennaType,
  } from '@dragonglass/telemetry/core';

  const TYPE_LABEL: Record<AntennaType, string> = {
    direct: 'Direct',
    relay: 'Relay',
    internal: 'Internal',
  };
  const TYPE_TONE: Record<AntennaType, 'off' | 'active' | 'info'> = {
    direct: 'active',
    relay: 'info',
    internal: 'off',
  };

  const { module, onInvokeEvent }: ModuleRendererProps = $props();
  const tx = $derived(module as PartModuleDataTransmitter);
  const typeTone = $derived(TYPE_TONE[tx.antennaType]);
  const typeLabel = $derived(TYPE_LABEL[tx.antennaType]);

  // Antenna range — stock's antennaPower is in metres. Format into
  // Mm / Gm so "2.5 Gm" reads at a glance vs "2500000000 m".
  const rangeStr = $derived.by(() => {
    const p = tx.antennaPower;
    if (p <= 0) return '—';
    if (p >= 1e9) return `${(p / 1e9).toFixed(2)} Gm`;
    if (p >= 1e6) return `${(p / 1e6).toFixed(1)} Mm`;
    if (p >= 1e3) return `${(p / 1e3).toFixed(0)} km`;
    return `${p.toFixed(0)} m`;
  });
  const ratePerSec = $derived(
    tx.packetInterval > 0 ? tx.packetSize / tx.packetInterval : 0,
  );
</script>

<section class="tx">
  <header class="tx__head">
    <span class="tx__name">ANTENNA</span>
    <span class="tx__badge" data-tone={typeTone}>{typeLabel}</span>
  </header>

  <div class="tx__row">
    <span class="tx__label">Range</span>
    <span class="tx__value">{rangeStr}</span>
  </div>
  <div class="tx__row">
    <span class="tx__label">Rate</span>
    <span class="tx__value">
      {ratePerSec.toFixed(1)}<em>Mits/s</em>
    </span>
  </div>

  <div class="tx__events">
    {#if tx.busy}
      <button
        type="button"
        class="tx__event tx__event--busy"
        onclick={() => onInvokeEvent('StopTransmission')}
        onpointerdown={(e) => e.stopPropagation()}
      >Stop Transmission</button>
    {:else}
      <button
        type="button"
        class="tx__event"
        onclick={() => onInvokeEvent('StartTransmission')}
        onpointerdown={(e) => e.stopPropagation()}
      >Transmit Data</button>
    {/if}
  </div>

  {#if tx.busy}
    <div class="tx__status" aria-live="polite">Transmitting…</div>
  {/if}
</section>

<style>
  .tx {
    margin-top: 8px;
    padding-top: 7px;
    border-top: 1px solid var(--line);
  }
  .tx__head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    margin-bottom: 5px;
  }
  .tx__name {
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.22em;
    color: var(--fg-dim);
    text-transform: uppercase;
  }
  .tx__badge {
    padding: 1px 6px;
    font-family: var(--font-mono);
    font-size: 8px;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: var(--fg-mute);
    border: 1px solid var(--line);
  }
  .tx__badge[data-tone='active'] {
    color: var(--accent);
    border-color: var(--accent);
    background: rgba(126, 245, 184, 0.12);
  }
  .tx__badge[data-tone='info'] {
    color: var(--info);
    border-color: var(--info);
    background: rgba(90, 176, 255, 0.12);
  }

  .tx__row {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 6px;
    font-size: 9px;
    min-height: 12px;
  }
  .tx__label {
    color: var(--fg-dim);
    letter-spacing: 0.06em;
    text-transform: uppercase;
    font-size: 8px;
  }
  .tx__value {
    color: var(--accent);
    font-family: var(--font-display);
    font-variant-numeric: tabular-nums;
    text-shadow: 0 0 4px var(--accent-glow);
  }
  .tx__value em {
    font-style: normal;
    color: var(--fg-mute);
    font-size: 8px;
    margin-left: 3px;
  }

  .tx__events {
    display: flex;
    margin-top: 5px;
  }
  .tx__event {
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
  .tx__event:hover {
    background: rgba(126, 245, 184, 0.16);
    border-color: var(--accent);
  }
  .tx__event--busy {
    color: var(--info);
    background: rgba(90, 176, 255, 0.1);
    border-color: var(--info);
  }
  .tx__event--busy:hover {
    background: rgba(90, 176, 255, 0.2);
  }
  .tx__status {
    margin-top: 4px;
    font-family: var(--font-mono);
    font-size: 8px;
    letter-spacing: 0.08em;
    color: var(--info);
    text-shadow: 0 0 3px var(--info-glow);
  }
</style>
