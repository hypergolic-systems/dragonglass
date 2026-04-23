<script lang="ts">
  // Bespoke renderer for ModuleLight. Compact — lights are tiny
  // controls usually bunched in fives. Shows a colour swatch that
  // takes on the light's configured RGB and dims when off, with a
  // single-button toggle.

  import type { ModuleRendererProps } from './types';
  import type { PartModuleLight } from '@dragonglass/telemetry/core';

  const { module, onInvokeEvent }: ModuleRendererProps = $props();
  const light = $derived(module as PartModuleLight);

  // Build the swatch's background colour straight from the light's
  // RGB channels. When off, we dim the swatch to a hint of its
  // on-state colour rather than full-black — keeps the pilot's eye
  // on where the colour will be when switched on.
  const swatchStyle = $derived.by(() => {
    const r = Math.round(light.r * 255);
    const g = Math.round(light.g * 255);
    const b = Math.round(light.b * 255);
    const glow = `rgba(${r},${g},${b},${light.on ? 0.6 : 0})`;
    const fill = light.on
      ? `rgb(${r},${g},${b})`
      : `rgba(${r},${g},${b},0.14)`;
    return `background:${fill}; box-shadow: 0 0 10px ${glow};`;
  });
</script>

<section class="light">
  <header class="light__head">
    <span class="light__name">LIGHT</span>
    <span class="light__swatch" style={swatchStyle} aria-hidden="true"></span>
  </header>

  <div class="light__events">
    <button
      type="button"
      class="light__event"
      onclick={() => onInvokeEvent('ToggleLights')}
      onpointerdown={(e) => e.stopPropagation()}
    >{light.on ? 'Lights Off' : 'Lights On'}</button>
  </div>
</section>

<style>
  .light {
    margin-top: 8px;
    padding-top: 7px;
    border-top: 1px solid var(--line);
  }

  .light__head {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-bottom: 5px;
  }

  .light__name {
    flex: 1 1 auto;
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.22em;
    color: var(--fg-dim);
    text-transform: uppercase;
  }

  /* Swatch: a pill that glows in the light's colour when on, dim
     when off. The glow is driven by `box-shadow` so it extends past
     the swatch edge, giving a phosphor-style halo. */
  .light__swatch {
    width: 26px;
    height: 12px;
    border: 1px solid var(--line-bright);
    transition: background 180ms ease, box-shadow 260ms ease;
  }

  .light__events {
    display: flex;
  }

  .light__event {
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
  .light__event:hover {
    background: rgba(126, 245, 184, 0.16);
    border-color: var(--accent);
    color: var(--accent-soft);
  }
</style>
