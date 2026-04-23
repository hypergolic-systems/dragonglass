<script lang="ts">
  // Bespoke renderer for ModuleEngines / ModuleEnginesFX.
  //
  // The typed engine wire shape carries only the engine-specific
  // readouts; generic events/fields are absent by design. This
  // renderer knows the ModuleEngines schema and invokes its fixed
  // KSP member names directly:
  //   - `invokeEvent('Activate')` when status == shutdown
  //   - `invokeEvent('Shutdown')` otherwise
  //   - `setField('thrustPercentage', 0..100)` for the limiter
  //
  // Layout, top to bottom:
  //   1. Header: "ENGINES" + colour-coded status badge
  //      (burning / idle / flameout / shutdown).
  //   2. Thrust gauge — a wide bar whose fill = currentThrust /
  //      maxThrust. Headline number is "current / max kN". A thin
  //      tick marks the thrust-limit setting so the pilot sees how
  //      much headroom the limiter is leaving on the table.
  //   3. ISP + thrust-limit slider (controls `thrustPercentage`
  //      directly via setField).
  //   4. Propellants — compact rows with ratio + available units.
  //   5. Activate / Shutdown button, selected by status.

  import type { ModuleRendererProps } from './types';
  import type { PartModuleEngines } from '@dragonglass/telemetry/core';

  const { module, onInvokeEvent, onSetField }: ModuleRendererProps = $props();
  const engine = $derived(module as PartModuleEngines);

  // Thrust gauge — fill fraction vs max. Guard against zero-thrust
  // engines (never-ignited) to avoid NaN.
  const thrustPct = $derived.by(() => {
    if (engine.maxThrust <= 0) return 0;
    return Math.max(0, Math.min(1, engine.currentThrust / engine.maxThrust)) * 100;
  });
  // Position of the thrust-limiter tick on the same 0-100% axis.
  const limitPct = $derived(Math.max(0, Math.min(100, engine.thrustLimit)));

  const thrustCurrent = $derived(fmtThrust(engine.currentThrust));
  const thrustMax = $derived(fmtThrust(engine.maxThrust));
  const ispStr = $derived(engine.realIsp.toFixed(0));

  function fmtThrust(kn: number): string {
    if (kn >= 1000) return (kn / 1000).toFixed(2) + 'M';
    if (kn >= 100) return kn.toFixed(0);
    return kn.toFixed(1);
  }

  function fmtAmount(v: number): string {
    if (v >= 1000) return v.toFixed(0);
    if (v >= 100) return v.toFixed(0);
    return v.toFixed(1);
  }

  // Propellants — sorted to put "dominant" resources (higher ratio)
  // first so the LF:Ox pair reads as "LF 1.1, Ox 0.9" rather than
  // alphabetical. Avoid the name `props` — it shadows Svelte's
  // `$props` rune in the type system's view of the scope.
  const propellants = $derived(
    [...engine.propellants].sort((a, b) => b.ratio - a.ratio),
  );
</script>

<section class="eng" data-status={engine.status}>
  <header class="eng__head">
    <span class="eng__name">ENGINES</span>
    <span class="eng__status" data-status={engine.status}>{engine.status}</span>
  </header>

  <!-- Thrust gauge. Long fill bar with the throttle limiter drawn as
       a thin vertical tick; the pilot sees both "where we are" and
       "where the limiter is set" at the same time. -->
  <div class="eng__gauge" role="presentation">
    <div class="eng__gauge-fill" style="--pct: {thrustPct}%"></div>
    <div class="eng__gauge-limit" style="--pct: {limitPct}%"></div>
  </div>
  <div class="eng__gauge-row">
    <span class="eng__label">THRUST</span>
    <span class="eng__gauge-num">
      {thrustCurrent}<em>/ {thrustMax} kN</em>
    </span>
  </div>

  <div class="eng__stats">
    <div class="eng__stat">
      <span class="eng__label">ISP</span>
      <span class="eng__stat-num">{ispStr}<em>s</em></span>
    </div>
  </div>

  <!-- Thrust limit: always 0..100 %, step 0.5 — baked into the
       renderer because the bespoke wire shape doesn't carry the
       UI_FloatRange bounds. ModuleEngines.thrustPercentage is the
       field name on the server side. -->
  <div class="eng__limit">
    <div class="eng__limit-head">
      <span class="eng__label">THRUST LIMIT</span>
      <span class="eng__stat-num">{engine.thrustLimit.toFixed(1)}<em>%</em></span>
    </div>
    <input
      class="eng__range"
      type="range"
      min="0"
      max="100"
      step="0.5"
      value={engine.thrustLimit}
      oninput={(e) => {
        const v = parseFloat((e.target as HTMLInputElement).value);
        if (Number.isFinite(v)) onSetField('thrustPercentage', v);
      }}
      onpointerdown={(e) => e.stopPropagation()}
    />
  </div>

  {#if propellants.length > 0}
    <div class="eng__props">
      <div class="eng__label">PROPELLANTS</div>
      {#each propellants as p (p.name)}
        <div class="eng__prop">
          <span class="eng__prop-name">{p.displayName}</span>
          <span class="eng__prop-ratio">×{p.ratio.toFixed(2)}</span>
          <span class="eng__prop-avail">
            {fmtAmount(p.totalAvailable)}<em>u</em>
          </span>
        </div>
      {/each}
    </div>
  {/if}

  <!-- Ignition button: name derived from status. Server re-verifies
       guiActive/active on every invoke, so clicking Shutdown on an
       already-shut-down engine drops silently. -->
  <div class="eng__events">
    {#if engine.status === 'shutdown'}
      <button
        type="button"
        class="eng__event"
        onclick={() => onInvokeEvent('Activate')}
        onpointerdown={(e) => e.stopPropagation()}
      >Activate Engine</button>
    {:else}
      <button
        type="button"
        class="eng__event"
        onclick={() => onInvokeEvent('Shutdown')}
        onpointerdown={(e) => e.stopPropagation()}
      >Shutdown Engine</button>
    {/if}
  </div>
</section>

<style>
  .eng {
    margin-top: 8px;
    padding-top: 7px;
    border-top: 1px solid var(--line);
  }

  .eng__head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    margin-bottom: 5px;
  }

  .eng__name {
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.22em;
    color: var(--fg-dim);
    text-transform: uppercase;
  }

  /* Status badge. Each value recolours the chip so the eye picks up
     the engine's state without reading the word. Muted for idle,
     accent-green + glow for burning, warn-amber for flameout,
     dim-grey for shutdown. */
  .eng__status {
    padding: 1px 6px;
    font-family: var(--font-mono);
    font-size: 8px;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: var(--fg-mute);
    border: 1px solid var(--line);
  }
  .eng__status[data-status='burning'] {
    color: var(--accent);
    border-color: var(--accent);
    background: rgba(126, 245, 184, 0.12);
    text-shadow: 0 0 4px var(--accent-glow);
  }
  .eng__status[data-status='idle'] {
    color: var(--fg-dim);
    border-color: var(--line-bright);
  }
  .eng__status[data-status='flameout'] {
    color: var(--warn);
    border-color: var(--warn);
    background: rgba(240, 180, 41, 0.12);
  }
  .eng__status[data-status='shutdown'] {
    color: var(--fg-mute);
  }

  /* Thrust gauge: long thin bar spanning the panel width. Fill uses
     the phosphor accent with a soft glow so it reads as a powered
     element; the limiter tick is a 2-px vertical line in info-blue
     so it contrasts with the fill even when the fill has passed
     the limit. */
  .eng__gauge {
    position: relative;
    height: 6px;
    background: rgba(46, 106, 85, 0.2);
    border: 1px solid var(--line);
    margin: 2px 0 3px;
  }
  .eng__gauge-fill {
    position: absolute;
    inset: 0 auto 0 0;
    width: var(--pct, 0%);
    background: var(--accent);
    box-shadow: 0 0 5px var(--accent-glow);
    transition: width 160ms linear;
  }
  /* Limit tick: 2 px wide pole on top of the fill. Absolute-positioned
     in percent so the container width doesn't matter. */
  .eng__gauge-limit {
    position: absolute;
    top: -3px;
    bottom: -3px;
    left: var(--pct, 100%);
    width: 2px;
    background: var(--info);
    box-shadow: 0 0 4px var(--info-glow);
    transform: translateX(-1px);
  }

  .eng__gauge-row {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 6px;
    min-height: 14px;
    font-size: 9px;
  }

  .eng__label {
    color: var(--fg-dim);
    letter-spacing: 0.08em;
    text-transform: uppercase;
    font-size: 8px;
  }

  .eng__gauge-num {
    color: var(--accent);
    font-family: var(--font-display);
    font-variant-numeric: tabular-nums;
    text-shadow: 0 0 4px var(--accent-glow);
  }
  .eng__gauge-num em {
    font-style: normal;
    color: var(--fg-mute);
    font-size: 8px;
    letter-spacing: 0.08em;
    margin-left: 3px;
  }

  .eng__stats {
    display: flex;
    gap: 12px;
    margin: 4px 0 6px;
  }

  .eng__stat {
    flex: 1 1 auto;
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 4px;
    font-size: 9px;
  }

  .eng__stat-num {
    color: var(--fg);
    font-family: var(--font-display);
    font-variant-numeric: tabular-nums;
  }
  .eng__stat-num em {
    font-style: normal;
    color: var(--fg-mute);
    font-size: 8px;
    letter-spacing: 0.08em;
    margin-left: 2px;
  }

  /* Propellants — compact three-column rows (name, ratio, available).
     Sorted by ratio so the headline propellant is at the top even
     on multi-prop engines. */
  .eng__props {
    display: flex;
    flex-direction: column;
    gap: 2px;
    margin: 4px 0 6px;
  }

  .eng__prop {
    display: grid;
    grid-template-columns: 1fr auto auto;
    gap: 6px;
    align-items: baseline;
    min-height: 13px;
    font-size: 9px;
  }

  .eng__prop-name {
    color: var(--fg);
    letter-spacing: 0.04em;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .eng__prop-ratio {
    color: var(--fg-dim);
    font-variant-numeric: tabular-nums;
    font-size: 8px;
  }

  .eng__prop-avail {
    color: var(--accent);
    font-variant-numeric: tabular-nums;
    font-family: var(--font-display);
    text-shadow: 0 0 4px var(--accent-glow);
  }
  .eng__prop-avail em {
    font-style: normal;
    color: var(--fg-mute);
    font-size: 8px;
    letter-spacing: 0.08em;
    margin-left: 2px;
  }

  /* Events — same compact button as DefaultModule so the two
     renderers read consistently. */
  .eng__events {
    display: flex;
    flex-wrap: wrap;
    gap: 4px;
    margin: 4px 0;
  }
  .eng__event {
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
  .eng__event:hover {
    background: rgba(126, 245, 184, 0.16);
    border-color: var(--accent);
    color: var(--accent-soft);
  }

  /* Thrust-limit slider: labeled head + phosphor-themed native range.
     Consistent with SliderField's styling but inline here because the
     widget's domain-specific (locked to thrustPercentage / 0..100). */
  .eng__limit {
    display: flex;
    flex-direction: column;
    gap: 1px;
    margin: 2px 0 6px;
  }

  .eng__limit-head {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 6px;
  }

  .eng__range {
    -webkit-appearance: none;
    appearance: none;
    width: 100%;
    height: 14px;
    background: transparent;
    cursor: pointer;
  }
  .eng__range::-webkit-slider-runnable-track {
    height: 4px;
    background: rgba(46, 106, 85, 0.35);
    border: 1px solid var(--line);
  }
  .eng__range::-webkit-slider-thumb {
    -webkit-appearance: none;
    width: 10px;
    height: 10px;
    margin-top: -4px;
    background: var(--accent);
    border: 1px solid var(--bg);
    box-shadow: 0 0 4px var(--accent-glow);
  }
</style>
