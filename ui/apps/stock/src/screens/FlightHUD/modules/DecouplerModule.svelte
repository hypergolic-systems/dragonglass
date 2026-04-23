<script lang="ts">
  // Bespoke renderer for ModuleDecouple + ModuleAnchoredDecoupler.
  //
  // Minimal by design: ejection force readout, a type glyph
  // (stack vs radial), and a big fat DECOUPLE button in alert red.
  // Post-firing the button is disabled and the badge flips to "Fired".
  //
  // Actions: invokeEvent('Decouple').

  import type { ModuleRendererProps } from './types';
  import type { PartModuleDecoupler } from '@dragonglass/telemetry/core';

  const { module, onInvokeEvent }: ModuleRendererProps = $props();
  const dec = $derived(module as PartModuleDecoupler);
  const kindLabel = $derived(dec.isAnchored ? 'Radial' : 'Stack');
</script>

<section class="dec" class:dec--fired={dec.isDecoupled}>
  <header class="dec__head">
    <span class="dec__name">DECOUPLER</span>
    <span class="dec__badge" data-tone={dec.isDecoupled ? 'off' : 'alert'}>
      {dec.isDecoupled ? 'Fired' : kindLabel}
    </span>
  </header>

  <div class="dec__row">
    <span class="dec__label">Ejection</span>
    <span class="dec__value">
      {dec.ejectionForce.toFixed(0)}<em>kN</em>
    </span>
  </div>

  <div class="dec__events">
    <button
      type="button"
      class="dec__event dec__event--alert"
      disabled={dec.isDecoupled}
      onclick={() => onInvokeEvent('Decouple')}
      onpointerdown={(e) => e.stopPropagation()}
    >{dec.isDecoupled ? 'Decoupled' : 'Decouple'}</button>
  </div>
</section>

<style>
  .dec {
    margin-top: 8px;
    padding-top: 7px;
    border-top: 1px solid var(--line);
  }

  .dec__head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    margin-bottom: 5px;
  }

  .dec__name {
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.22em;
    color: var(--fg-dim);
    text-transform: uppercase;
  }

  .dec__badge {
    padding: 1px 6px;
    font-family: var(--font-mono);
    font-size: 8px;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: var(--fg-mute);
    border: 1px solid var(--line);
  }
  .dec__badge[data-tone='alert'] {
    color: var(--alert);
    border-color: var(--alert);
    background: rgba(255, 82, 82, 0.12);
  }

  .dec__row {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 6px;
    font-size: 9px;
    min-height: 13px;
    margin-bottom: 4px;
  }
  .dec__label {
    color: var(--fg-dim);
    letter-spacing: 0.06em;
    text-transform: uppercase;
    font-size: 8px;
  }
  .dec__value {
    color: var(--accent);
    font-family: var(--font-display);
    font-variant-numeric: tabular-nums;
    text-shadow: 0 0 4px var(--accent-glow);
  }
  .dec__value em {
    font-style: normal;
    color: var(--fg-mute);
    font-size: 8px;
    margin-left: 3px;
  }

  .dec__events { display: flex; }

  .dec__event {
    flex: 1 1 auto;
    padding: 3px 8px;
    min-height: 22px;
    font-family: var(--font-mono);
    font-size: 9px;
    letter-spacing: 0.08em;
    text-transform: uppercase;
    cursor: pointer;
    transition: background 160ms ease, border-color 160ms ease;
  }
  .dec__event--alert {
    color: var(--alert);
    background: rgba(255, 82, 82, 0.08);
    border: 1px solid var(--alert);
  }
  .dec__event--alert:hover:not(:disabled) {
    background: rgba(255, 82, 82, 0.2);
    color: var(--fg);
  }
  .dec__event:disabled {
    opacity: 0.4;
    cursor: default;
  }
</style>
