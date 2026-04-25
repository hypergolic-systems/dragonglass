<script lang="ts">
  // The Propulsion panel — sole occupant of the left staging stack
  // (for now). Layout, top to bottom:
  //
  //   1. Header strip.
  //   2. Readout stack:
  //      - ΔV STAGE (hero)         — most actionable Δv for the pilot
  //      - ΔV TOTAL                — remaining mission Δv
  //      - TWR                     — stage thrust-to-weight
  //      - THROTTLE                — pilot input, bar + %
  //   3. Fuel-group gauges. Engines are partitioned into fuel groups
  //      by matching crossfeed reach + propellant set (see
  //      `engine-groups.ts`). Each group gets a small icon
  //      highlighting its engines and one-or-more fuel bars. A
  //      matched-proportion group renders a single unified bar
  //      (e.g. "LFO"); an imbalanced group splits into per-propellant
  //      bars so drain mismatches are visible.
  //   4. Engine rosette. Orthographic bottom-up plot of every engine
  //      on the vessel, sized ∝ √thrust, colour-coded by status.
  //
  // Data comes from two telemetry topics:
  //   - `useFlightData()`   — throttle, deltaVMission, deltaVStage,
  //                            twrStage, stageIdx
  //   - `useEngineData()`   — per-engine position, status, thrust,
  //                            Isp, crossfeed reach, and propellant
  //                            levels. The fuel-group partitioning
  //                            is computed locally via `groupEngines`
  //                            in `engine-groups.ts`.

  import {
    useFlightData,
    useEngineData,
  } from '@dragonglass/telemetry/svelte';
  import type { EngineGroup } from '@dragonglass/telemetry/core';
  import EngineIcon from './EngineIcon.svelte';
  import { idealizeEngineMap } from './engine-map';
  import { groupEngines } from './engine-groups';
  import { formatDeltaV, formatTwr } from './format';
  import { unifiedResourceLabel } from './resource-names';
  import './Propulsion.css';

  const s = useFlightData();
  const e = useEngineData();

  const groups = $derived(groupEngines(e.engines));

  const dvStage = $derived(formatDeltaV(s.deltaVStage));
  const dvTotal = $derived(formatDeltaV(s.deltaVMission));
  const twrText = $derived(formatTwr(s.twrStage));
  const throttlePct = $derived(Math.round(s.throttle * 100));
  const throttleClamped = $derived(Math.max(0, Math.min(100, throttlePct)));
  const layout = $derived(idealizeEngineMap(e.engines));

  const totalCount = $derived(e.engines.length);

  const dvStageMissing = $derived(dvStage.value === '—');
  const dvTotalMissing = $derived(dvTotal.value === '—');
  const twrMissing = $derived(twrText === '—');

  // ─── Throttle arc geometry ─────────────────────────────────────
  //
  // The arc wraps the engine rosette in the rosette's SVG viewBox.
  // It uses a "nav" angle convention — 0° at the top, clockwise
  // positive — so throttle position reads directly as clock time:
  // 0% sits at 7:30 (-135°), 50% straight up, 100% at 4:30 (+135°).
  //
  // The arc is split into four equal segments separated by narrow
  // gaps at the 25 / 50 / 75 % marks, so the 50 % gap lands right
  // at the top for visual symmetry. Each segment is rendered twice
  // — a muted track and an accent fill — with the fill only drawn
  // up to the current throttle position within the segment.
  const THROTTLE_ARC_R = 0.97;
  const ARC_TOTAL_DEG = 270;
  const ARC_START_DEG = -135;
  const SEG_COUNT = 4;
  // Gap has to overcome the stroke width to read as a break — at
  // ~100 px render size the stroke is ~2 CSS px, and a 3° arc
  // chord is ~2.6 px, which nets to sub-pixel. 7° gives ~6 px of
  // clear darkness between lit segments.
  const SEG_GAP_DEG = 7;
  const SEG_SPAN_DEG = (ARC_TOTAL_DEG - (SEG_COUNT - 1) * SEG_GAP_DEG) / SEG_COUNT;

  // User-angle convention (0°=top, CW positive) → SVG point.
  function angleToPoint(r: number, deg: number): [number, number] {
    const rad = (deg * Math.PI) / 180;
    return [r * Math.sin(rad), -r * Math.cos(rad)];
  }

  // SVG arc path between two absolute user angles on radius r.
  // The throttle arc sweeps clockwise in the visual sense (i.e.
  // CW in user convention); SVG sweep-flag 1 matches that.
  function arcPath(r: number, startDeg: number, endDeg: number): string {
    const delta = endDeg - startDeg;
    if (Math.abs(delta) < 1e-3) return '';
    const [x1, y1] = angleToPoint(r, startDeg);
    const [x2, y2] = angleToPoint(r, endDeg);
    const largeArc = Math.abs(delta) > 180 ? 1 : 0;
    return `M ${x1.toFixed(4)} ${y1.toFixed(4)} A ${r} ${r} 0 ${largeArc} 1 ${x2.toFixed(4)} ${y2.toFixed(4)}`;
  }

  const throttleArcPos = $derived((throttleClamped / 100) * ARC_TOTAL_DEG);

  const throttleSegments = $derived(
    Array.from({ length: SEG_COUNT }, (_, i) => {
      const segStartPos = i * (SEG_SPAN_DEG + SEG_GAP_DEG);
      const segEndPos = segStartPos + SEG_SPAN_DEG;
      const startAbs = ARC_START_DEG + segStartPos;
      const endAbs = ARC_START_DEG + segEndPos;
      const track = arcPath(THROTTLE_ARC_R, startAbs, endAbs);

      // Fill covers the segment up to wherever the throttle-arc
      // cursor sits. Throttle in the gap between segments means
      // this segment is either fully full (gap just after) or
      // fully empty (gap just before).
      let fill = '';
      if (throttleArcPos >= segEndPos) {
        fill = track;
      } else if (throttleArcPos > segStartPos) {
        const fillEndAbs = ARC_START_DEG + throttleArcPos;
        fill = arcPath(THROTTLE_ARC_R, startAbs, fillEndAbs);
      }
      return { track, fill };
    }),
  );

  type GaugeState = 'nominal' | 'warn' | 'alert';

  interface Gauge {
    label: string;
    pct: number;       // 0..100
    state: GaugeState;
  }

  // Fuel-remaining thresholds — amber at 30 %, red at 10 %. Mirrors
  // the stock KSP low-fuel warning cadence and leaves enough
  // margin below `warn` for the pilot to see the state change
  // before crossing into `alert`.
  function stateFor(pct: number): GaugeState {
    if (pct < 10) return 'alert';
    if (pct < 30) return 'warn';
    return 'nominal';
  }

  // Unified-or-split decision. When every propellant in the group
  // sits at the same fraction of capacity (within 2 %), a single
  // bar conveys the fuel state — stock LF+Ox draining together
  // collapses to one "LFO" bar. Otherwise each propellant gets
  // its own bar so imbalances are visible at a glance.
  function renderGauges(group: EngineGroup): Gauge[] {
    if (group.propellants.length === 0) return [];
    // Snap near-zero residuals to 0. After flameout KSP tanks often
    // hold a sub-unit residual that reads as ~0.01 % — enough to
    // leak the alert-state bar's red box-shadow glow through an
    // otherwise-empty gauge.
    const fractions = group.propellants.map((p) => {
      if (p.capacity <= 0) return 0;
      const f = p.available / p.capacity;
      return f < 0.001 ? 0 : f;
    });
    const first = fractions[0];
    const matched =
      group.propellants.length > 1 &&
      fractions.every((f) => Math.abs(f - first) < 0.02);
    if (matched) {
      const pct = first * 100;
      return [
        {
          label: unifiedResourceLabel(group.propellants.map((p) => p.abbr)),
          pct,
          state: stateFor(pct),
        },
      ];
    }
    return group.propellants.map((p, i) => {
      const pct = fractions[i] * 100;
      return {
        label: p.abbr,
        pct,
        state: stateFor(pct),
      };
    });
  }

  const fuelRows = $derived(
    groups.map((g, i) => ({
      key: `${i}:${g.engineIds.join(',')}`,
      group: g,
      gauges: renderGauges(g),
    })),
  );
</script>

<section class="prop" aria-label="Propulsion">
  <div class="prop__stats">
    <div class="prop__row">
      <span class="prop__label">&#916;V STAGE</span>
      <span class="prop__value">
        <span class="prop__num" class:prop__num--null={dvStageMissing}>{dvStage.value}</span>
      </span>
    </div>
    <div class="prop__row">
      <span class="prop__label">&#916;V TOTAL</span>
      <span class="prop__value">
        <span class="prop__num" class:prop__num--null={dvTotalMissing}>{dvTotal.value}</span>
      </span>
    </div>
    <div class="prop__row">
      <span class="prop__label">TWR</span>
      <span class="prop__value">
        <span class="prop__num" class:prop__num--null={twrMissing}>{twrText}</span>
      </span>
    </div>
  </div>

  {#if fuelRows.length > 0}
    <div class="prop__fuel">
      {#each fuelRows as row (row.key)}
        <div class="prop__fuel-row">
          <EngineIcon groupIds={row.group.engineIds} />
          <div class="prop__fuel-gauges">
            {#each row.gauges as gauge}
              <div class="prop__fuel-gauge prop__fuel-gauge--{gauge.state}">
                <span class="prop__fuel-label">{gauge.label}</span>
                <div class="prop__fuel-bar" role="presentation">
                  {#if gauge.pct > 0}
                    <div
                      class="prop__fuel-bar-fill"
                      style="--pct: {Math.min(100, gauge.pct)}%"
                    ></div>
                  {/if}
                  {#if gauge.state !== 'nominal'}
                    <span class="prop__fuel-bar-pct">{Math.round(gauge.pct)}%</span>
                  {/if}
                </div>
              </div>
            {/each}
          </div>
        </div>
      {/each}
    </div>
  {/if}

  <figure class="prop__map">
    <svg
      class="prop__map-svg"
      viewBox="-1 -1 2 2"
      preserveAspectRatio="xMidYMid meet"
      aria-hidden="true"
    >
      <!-- Inner guide ring — subtle frame for the rosette. The
           old outer ring is replaced by the throttle arc below. -->
      <circle class="prop__map-ring prop__map-ring--inner" cx="0" cy="0" r="0.46" />

      <!-- Throttle arc: four segments wrapping the rosette, with
           gaps at the 25/50/75 % marks. 50% = straight up; the
           gap at the top is the visual anchor. -->
      {#each throttleSegments as seg}
        <path class="prop__throttle-track" d={seg.track} />
      {/each}
      {#each throttleSegments as seg}
        {#if seg.fill}
          <path class="prop__throttle-fill" d={seg.fill} />
        {/if}
      {/each}

      <!-- Numeric throttle readout, centered below the rosette
           in the gap at the arc's open bottom. -->
      <text
        class="prop__throttle-num"
        x="0"
        y="0.92"
        text-anchor="middle"
        dominant-baseline="central"
      >
        {throttleClamped}<tspan class="prop__throttle-num-unit">%</tspan>
      </text>

      {#each layout.points as p, i (p.id)}
        <!-- Radio-button anatomy: an outer stroked ring identifies
             the engine's physical footprint (size ∝ √thrust) and an
             inner filled dot represents the ignited flame when
             present. The gap between them is a fixed viewBox
             quantity (not a proportion of the engine radius), so
             the radio-button silhouette stays consistent whether
             the cluster is scaled up to fill the panel (sparse
             layouts) or shrunk down to fit (dense stacks).

             Dot brightness tracks the engine's actual throttle —
             0 hides the dot entirely (no flame), and any throttle
             above 0 maps linearly into [0.25, 1.0] opacity so even
             a low throttle still reads as a lit bell rather than
             vanishing into the muted ring. The throttle already
             accounts for per-engine thrust limiters, so a single
             limited engine in a cluster reads dimmer than its
             siblings. Failed engines override this with their own
             pulsing red dot — see the CSS. -->
        <g
          class="prop__engine prop__engine--{p.status}"
          style="--i: {i}; --dot-opacity: {p.throttle > 0 ? 0.25 + 0.75 * p.throttle : 0};"
        >
          <circle class="prop__engine-ring" cx={p.cx} cy={p.cy} r={p.r} />
          <circle
            class="prop__engine-dot"
            cx={p.cx}
            cy={p.cy}
            r={Math.max(0.02, p.r - 0.04)}
          />
        </g>
      {/each}

      {#if totalCount === 0}
        <text class="prop__map-empty" x="0" y="0.04" text-anchor="middle">NO ENGINES</text>
      {/if}
    </svg>
  </figure>
</section>
