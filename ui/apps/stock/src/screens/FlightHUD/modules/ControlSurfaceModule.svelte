<script lang="ts">
  // Bespoke renderer for ModuleControlSurface (wings + airbrakes,
  // since ModuleAeroSurface subclasses this). Stock's six toggles +
  // authority limiter all round-trip via setField.
  //
  // Layout: three P/Y/R axis pills (on = axis respected, off = axis
  // ignored), deploy toggle + invert, authority slider.
  //
  // Actions: setField on each corresponding KSPField.

  import type { ModuleRendererProps } from './types';
  import type { PartModuleControlSurface } from '@dragonglass/telemetry/core';

  const { module, onSetField }: ModuleRendererProps = $props();
  const ctrl = $derived(module as PartModuleControlSurface);
  const authorityPct = $derived(Math.max(0, Math.min(100, ctrl.authorityLimiter)));
  const pitchOn = $derived(!ctrl.ignorePitch);
  const yawOn = $derived(!ctrl.ignoreYaw);
  const rollOn = $derived(!ctrl.ignoreRoll);

  function seekAuthority(e: MouseEvent) {
    const r = (e.currentTarget as HTMLElement).getBoundingClientRect();
    const pct = Math.max(0, Math.min(100, ((e.clientX - r.left) / r.width) * 100));
    onSetField('authorityLimiter', pct);
  }
</script>

<section class="ctrl">
  <header class="ctrl__head">
    <span class="ctrl__name">CTRL SURFACE</span>
    <span class="ctrl__badge" data-tone={ctrl.deploy ? 'warn' : 'off'}>
      {ctrl.deploy ? 'Deployed' : 'Stowed'}
    </span>
  </header>

  <!-- Axis pills. Stock actually stores `ignoreX = true` when the
       axis is disabled, so we invert for the UI: pill lit = axis is
       respected. Clicking writes the inverted bool back. -->
  <div class="ctrl__axes">
    <button
      type="button"
      class="ctrl__axis"
      class:ctrl__axis--on={pitchOn}
      onclick={() => onSetField('ignorePitch', !ctrl.ignorePitch)}
      onpointerdown={(e) => e.stopPropagation()}
      title={pitchOn ? 'Pitch enabled' : 'Pitch disabled'}
    >Pitch</button>
    <button
      type="button"
      class="ctrl__axis"
      class:ctrl__axis--on={yawOn}
      onclick={() => onSetField('ignoreYaw', !ctrl.ignoreYaw)}
      onpointerdown={(e) => e.stopPropagation()}
      title={yawOn ? 'Yaw enabled' : 'Yaw disabled'}
    >Yaw</button>
    <button
      type="button"
      class="ctrl__axis"
      class:ctrl__axis--on={rollOn}
      onclick={() => onSetField('ignoreRoll', !ctrl.ignoreRoll)}
      onpointerdown={(e) => e.stopPropagation()}
      title={rollOn ? 'Roll enabled' : 'Roll disabled'}
    >Roll</button>
  </div>

  <div class="ctrl__row">
    <span class="ctrl__label">Authority</span>
    <span class="ctrl__value">{authorityPct.toFixed(0)}<em>%</em></span>
  </div>
  <!-- svelte-ignore a11y_click_events_have_key_events -->
  <!-- svelte-ignore a11y_no_static_element_interactions -->
  <div class="ctrl__bar" onclick={seekAuthority} onpointerdown={(e) => e.stopPropagation()}>
    <div class="ctrl__bar-fill" style="--pct: {authorityPct}%"></div>
  </div>

  <div class="ctrl__row ctrl__row--toggles">
    <button
      type="button"
      class="ctrl__toggle"
      class:ctrl__toggle--on={ctrl.deploy}
      onclick={() => onSetField('deploy', !ctrl.deploy)}
      onpointerdown={(e) => e.stopPropagation()}
    >Deploy</button>
    <button
      type="button"
      class="ctrl__toggle"
      class:ctrl__toggle--on={ctrl.deployInvert}
      onclick={() => onSetField('deployInvert', !ctrl.deployInvert)}
      onpointerdown={(e) => e.stopPropagation()}
    >Invert</button>
  </div>
</section>

<style>
  .ctrl {
    margin-top: 8px;
    padding-top: 7px;
    border-top: 1px solid var(--line);
  }
  .ctrl__head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    margin-bottom: 5px;
  }
  .ctrl__name {
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.22em;
    color: var(--fg-dim);
    text-transform: uppercase;
  }
  .ctrl__badge {
    padding: 1px 6px;
    font-family: var(--font-mono);
    font-size: 8px;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: var(--fg-mute);
    border: 1px solid var(--line);
  }
  .ctrl__badge[data-tone='warn'] {
    color: var(--warn);
    border-color: var(--warn);
    background: rgba(240, 180, 41, 0.12);
  }

  .ctrl__axes {
    display: grid;
    grid-template-columns: repeat(3, 1fr);
    gap: 3px;
    margin-bottom: 5px;
  }
  .ctrl__axis {
    padding: 3px 0;
    font-family: var(--font-mono);
    font-size: 8px;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: var(--fg-mute);
    background: transparent;
    border: 1px solid var(--line);
    cursor: pointer;
  }
  .ctrl__axis--on {
    color: var(--accent);
    border-color: var(--accent);
    background: rgba(126, 245, 184, 0.12);
    text-shadow: 0 0 3px var(--accent-glow);
  }

  .ctrl__row {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 6px;
    font-size: 9px;
    min-height: 12px;
  }
  .ctrl__row--toggles {
    align-items: center;
    gap: 4px;
    margin-top: 4px;
  }
  .ctrl__label {
    color: var(--fg-dim);
    letter-spacing: 0.06em;
    text-transform: uppercase;
    font-size: 8px;
  }
  .ctrl__value {
    color: var(--accent);
    font-family: var(--font-display);
    font-variant-numeric: tabular-nums;
    text-shadow: 0 0 4px var(--accent-glow);
  }
  .ctrl__value em {
    font-style: normal;
    color: var(--fg-mute);
    font-size: 8px;
    margin-left: 3px;
  }
  .ctrl__bar {
    position: relative;
    height: 4px;
    background: rgba(46, 106, 85, 0.2);
    border: 1px solid var(--line);
    margin: 1px 0 3px;
    cursor: pointer;
  }
  .ctrl__bar-fill {
    position: absolute;
    inset: 0 auto 0 0;
    width: var(--pct, 0%);
    background: var(--accent);
    box-shadow: 0 0 5px var(--accent-glow);
    transition: width 140ms ease;
  }

  .ctrl__toggle {
    flex: 1 1 0;
    padding: 3px 6px;
    font-family: var(--font-mono);
    font-size: 8px;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: var(--fg-mute);
    background: transparent;
    border: 1px solid var(--line);
    cursor: pointer;
  }
  .ctrl__toggle--on {
    color: var(--warn);
    border-color: var(--warn);
    background: rgba(240, 180, 41, 0.12);
  }
</style>
