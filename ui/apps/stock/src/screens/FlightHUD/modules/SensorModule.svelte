<script lang="ts">
  // Bespoke renderer for ModuleEnviroSensor — the stock 2HOT
  // Thermometer / PresMat Barometer / GRAVMAX Gravioli Detector /
  // Double-C Seismic Accelerometer all share this module; their
  // `sensorType` selects the reading the server computes.
  //
  // Hero visualisation: a big numeric readout with its unit, plus a
  // colour-coded status pill and an Active/Off toggle. When the
  // sensor has no valid reading (e.g. gravimeter out of range,
  // barometer in vacuum), the hero number is suppressed and the
  // status text takes its place — mirroring stock's "Out of Range"
  // / "No Atm" readoutInfo vocabulary.
  //
  // Server schema reminder: `invokeEvent(moduleIndex, 'Toggle')`
  // flips `ModuleEnviroSensor.sensorActive`.

  import type { ModuleRendererProps } from './types';
  import type {
    PartModuleEnviroSensor,
    EnviroSensorType,
  } from '@dragonglass/telemetry/core';

  const SENSOR_LABELS: Record<EnviroSensorType, string> = {
    temperature: 'Temperature',
    pressure: 'Pressure',
    gravity: 'Gravity',
    acceleration: 'Acceleration',
  };

  const { module, onInvokeEvent }: ModuleRendererProps = $props();
  const sensor = $derived(module as PartModuleEnviroSensor);

  // Reading is only meaningful when statusText is "Active" and a
  // unit is present — otherwise we render the status text as the
  // hero (e.g. "No Atm" for a barometer in vacuum).
  const hasReading = $derived(sensor.statusText === 'Active' && sensor.unit !== '');

  const valueStr = $derived(formatValue(sensor.sensorType, sensor.value));
  const sensorLabel = $derived(SENSOR_LABELS[sensor.sensorType] ?? sensor.sensorType);

  const statusTone = $derived.by(() => {
    if (!sensor.active) return 'off';
    if (sensor.statusText === 'Active') return 'active';
    return 'warn';  // "Out of Range", "No Atm", "Trace Atm"
  });

  // Formatting per sensor type — decimals tuned to the typical
  // dynamic range. Temperature: 1 decimal (279.5 K). Pressure: 2
  // decimals (101.33 kPa) because low values near zero need them.
  // Gravity / Acceleration: 3 decimals (9.807 m/s² / 2.345 g).
  function formatValue(kind: EnviroSensorType, v: number): string {
    switch (kind) {
      case 'temperature': return v.toFixed(1);
      case 'pressure':    return v.toFixed(v >= 10 ? 2 : 3);
      case 'gravity':     return v.toFixed(3);
      case 'acceleration': return v.toFixed(3);
      default:            return v.toFixed(2);
    }
  }
</script>

<section class="sense">
  <header class="sense__head">
    <span class="sense__name">SENSOR</span>
    <span class="sense__sensor-type">{sensorLabel}</span>
  </header>

  <!-- Hero numeric / status readout. When the reading isn't valid
       we swap in the status text at a slightly smaller size so the
       eye reads "No Atm" / "Out of Range" as the headline instead
       of a stale 0.0 number. -->
  <div class="sense__hero">
    {#if hasReading}
      <span class="sense__value">{valueStr}</span>
      <span class="sense__unit">{sensor.unit}</span>
    {:else}
      <span class="sense__status-hero">{sensor.statusText}</span>
    {/if}
  </div>

  <div class="sense__row">
    <span class="sense__label">STATUS</span>
    <span class="sense__pill" data-tone={statusTone}>
      {sensor.active ? sensor.statusText : 'Off'}
    </span>
  </div>

  <div class="sense__events">
    <button
      type="button"
      class="sense__event"
      onclick={() => onInvokeEvent('Toggle')}
      onpointerdown={(e) => e.stopPropagation()}
    >{sensor.active ? 'Deactivate Sensor' : 'Activate Sensor'}</button>
  </div>
</section>

<style>
  .sense {
    margin-top: 8px;
    padding-top: 7px;
    border-top: 1px solid var(--line);
  }

  .sense__head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    margin-bottom: 6px;
  }

  .sense__name {
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.22em;
    color: var(--fg-dim);
    text-transform: uppercase;
  }

  .sense__sensor-type {
    font-family: var(--font-mono);
    font-size: 8px;
    letter-spacing: 0.16em;
    text-transform: uppercase;
    color: var(--accent);
    text-shadow: 0 0 4px var(--accent-glow);
  }

  /* Hero readout — roomy, centered, phosphor tube. Value and unit
     baseline-share; tabular-nums so the digits don't jiggle while
     the sensor samples per-tick. */
  .sense__hero {
    display: flex;
    align-items: baseline;
    justify-content: center;
    gap: 6px;
    padding: 10px 6px 8px;
    min-height: 34px;
    background: rgba(126, 245, 184, 0.03);
    border: 1px solid var(--line-accent);
    box-shadow: inset 0 0 8px rgba(126, 245, 184, 0.06);
    margin-bottom: 6px;
  }

  .sense__value {
    font-family: var(--font-display);
    font-size: 26px;
    line-height: 1;
    color: var(--accent);
    font-variant-numeric: tabular-nums;
    letter-spacing: 0.02em;
    text-shadow: 0 0 10px var(--accent-glow);
  }

  .sense__unit {
    font-family: var(--font-mono);
    font-size: 10px;
    color: var(--fg-dim);
    letter-spacing: 0.14em;
    text-transform: uppercase;
  }

  /* Swap-in text when no numeric reading is possible. Smaller than
     the numeric so it doesn't overflow the panel on long phrases
     like "Not in Atmosphere" / "Out of Range". */
  .sense__status-hero {
    font-family: var(--font-display);
    font-size: 14px;
    letter-spacing: 0.14em;
    color: var(--warn);
    text-transform: uppercase;
    text-shadow: 0 0 6px var(--warn-glow);
  }

  .sense__row {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 6px;
    min-height: 14px;
    margin-bottom: 6px;
  }

  .sense__label {
    color: var(--fg-dim);
    letter-spacing: 0.08em;
    text-transform: uppercase;
    font-size: 8px;
  }

  /* Status pill — mirrors the engine status-badge colour scheme so
     a pilot reading multiple bespoke modules on the same vessel
     sees a consistent vocabulary. */
  .sense__pill {
    padding: 1px 8px;
    font-family: var(--font-mono);
    font-size: 8px;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: var(--fg-mute);
    border: 1px solid var(--line);
  }
  .sense__pill[data-tone='active'] {
    color: var(--accent);
    border-color: var(--accent);
    background: rgba(126, 245, 184, 0.12);
    text-shadow: 0 0 4px var(--accent-glow);
  }
  .sense__pill[data-tone='warn'] {
    color: var(--warn);
    border-color: var(--warn);
    background: rgba(240, 180, 41, 0.1);
  }
  .sense__pill[data-tone='off'] {
    color: var(--fg-mute);
  }

  .sense__events {
    display: flex;
  }

  .sense__event {
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
  .sense__event:hover {
    background: rgba(126, 245, 184, 0.16);
    border-color: var(--accent);
    color: var(--accent-soft);
  }
</style>
