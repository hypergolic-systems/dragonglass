import { formatAltLabel, formatSpeedLabel } from './format';

export interface Tick {
  value: number;
  deltaDeg: number;
  label: string;
  major: boolean;
}

export interface TapeScale {
  posDeg: (v: number) => number;
  valueAt: (deg: number) => number;
  formatLabel: (v: number) => string;
  /**
   * Generate only the ticks inside the visible arc around `value`.
   * Ticks come from the linear regimes the visible window overlaps —
   * transition zones contribute nothing, so the tick grid of a given
   * regime is fixed for every cursor position that regime is ever
   * visible at. A tick never moves, never retiers, never fades; as
   * the cursor slides, ticks scroll in and out of the arc and that's
   * the entire animation.
   */
  visibleTicks: (value: number, halfArcDeg: number) => Tick[];
}

/**
 * One linear regime on the tape. Owns a contiguous value range and
 * its own tick schedule. The tape's degree-per-unit scaling is
 * auto-derived from `majorStep` so every major tick — in every
 * regime, and *across* regime transitions — is exactly
 * `MAJOR_ARC_DEG` apart on the tape arc. Minor ticks interpolate
 * between majors according to whatever `minorStep` the regime
 * specifies; denser regimes just have more minors per major.
 *
 * Override `dpu` only to deliberately compress or stretch a regime.
 *
 * Regimes are always disjoint; gaps between them get auto-generated
 * cubic-Hermite transition curves. The transition's arc is chosen
 * so that `lastMajor(A)` and `firstMajor(B)` land exactly
 * `MAJOR_ARC_DEG` apart too — so the major rhythm is uniform end-to-
 * end, with only the non-linear interpolation between them marking
 * the boundary.
 */
export interface LinearRegime {
  vLo: number;
  vHi: number;
  minorStep: number;
  majorStep: number;
  dpu?: number;
}

/**
 * Arc (in degrees) between adjacent major ticks, everywhere on the
 * tape. 24° means roughly 4 majors inside the 96° visible arc.
 */
const MAJOR_ARC_DEG = 24;

// Internal representation of each run of the tape, whether a user-
// supplied linear regime or a computed cubic transition between two.
interface Segment {
  kind: 'linear' | 'cubic';
  vLo: number;
  vHi: number;
  startDeg: number;
  endDeg: number;
  posDeg: (v: number) => number;
  valueAt: (deg: number) => number;
  minorStep?: number;
  majorStep?: number;
}

/**
 * Build a piecewise tape scale from a list of disjoint linear
 * regimes. Gaps between adjacent regimes get cubic-Hermite
 * transitions: C1-continuous at both ends, no ticks of their own,
 * `posDeg` is a plain cubic in `v`, and `valueAt` falls back to
 * bisection (60 iterations, sub-pixel accurate well past `1e-9`).
 *
 * We use cubic Hermite rather than pure log because log-with-
 * derivative-match can only satisfy C1 at both ends when
 * `vHi_A·dpu_A == vLo_B·dpu_B`, which over-constrains the step
 * schedule. The cubic version decouples regime step choice from
 * transition shape.
 */
export function buildScale(
  regimes: LinearRegime[],
  formatLabel: (v: number) => string,
): TapeScale {
  const sorted = [...regimes].sort((a, b) => a.vLo - b.vLo);
  const segments: Segment[] = [];
  let cursorDeg = 0;

  const dpuFor = (r: LinearRegime) => r.dpu ?? MAJOR_ARC_DEG / r.majorStep;

  for (let i = 0; i < sorted.length; i++) {
    const r = sorted[i];
    const rDpu = dpuFor(r);
    const next = sorted[i + 1];

    // Whether we need a cubic transition at all. Only when the next
    // regime's dpu differs — adjacent regimes with identical dpu
    // join without any discontinuity in posDeg's slope, so a
    // straight-through linear sweep is correct.
    const nextDpu = next ? dpuFor(next) : 0;
    const needTransition = !!next && nextDpu !== rDpu;

    // Absorb regime A's last *two* majors into the cubic transition.
    // The linear portion ends at `r.vHi - 2·majorStep`, which gives
    // the cubic enough value range to interpolate `dpu` linearly from
    // `dpuA` to `dpuB` without overshoot. Regime A's penultimate
    // major (`r.vHi - majorStep`) still renders — it just lives on
    // the cubic now, at whatever arc position `posDeg_cubic` maps it
    // to (slightly offset from the regular regime A major rhythm).
    // Regime A's outer boundary `r.vHi` is handled as the first major
    // of regime B (= `next.vLo`).
    let linearVHi = r.vHi;
    if (needTransition) {
      const twoMajor = r.vHi - 2 * r.majorStep;
      const oneMajor = r.vHi - r.majorStep;
      if (twoMajor >= r.vLo) linearVHi = twoMajor;
      else if (oneMajor >= r.vLo) linearVHi = oneMajor;
    }

    const arc = (linearVHi - r.vLo) * rDpu;
    const sStart = cursorDeg;
    const sEnd = sStart + arc;
    segments.push({
      kind: 'linear',
      vLo: r.vLo,
      vHi: linearVHi,
      startDeg: sStart,
      endDeg: sEnd,
      posDeg: (v) => sStart + (v - r.vLo) * rDpu,
      valueAt: (deg) => r.vLo + (deg - sStart) / rDpu,
      minorStep: r.minorStep,
      majorStep: r.majorStep,
    });
    cursorDeg = sEnd;

    if (needTransition) {
      const vLo = linearVHi;
      const vHi = next!.vLo;
      const range = vHi - vLo;
      if (range > 0) {
      const dpuA = rDpu;
      const dpuB = nextDpu;
      // Hermite cubic on `t = (v - vLo) / range`:
      //   h(t) = H10·range·dpuA + H01·transArc + H11·range·dpuB
      // with `H10 = t³ − 2t² + t`, `H01 = −2t³ + 3t²`,
      // `H11 = t³ − t²`. h(0) = 0, h(1) = transArc; h′(0) / range
      // = dpuA, h′(1) / range = dpuB (C1 continuity at both ends).
      //
      // Setting `transArc = range·(dpuA + dpuB)/2` zeroes the t³
      // coefficient of h, so the cubic degenerates into a quadratic
      // whose derivative (dpu) is *linear* in t — a constant
      // rate-of-change from dpuA to dpuB across the transition, with
      // no overshoot bump. Side effect: the tape arc between regime
      // A's last linear major and regime B's first major is no
      // longer MAJOR_ARC_DEG — for a 10× dpu ratio it comes out to
      // ~55% of it. Transitions read as visually shorter major gaps,
      // but the motion is smooth the whole way through.
      const transArc = (range * (dpuA + dpuB)) / 2;
      const tStart = cursorDeg;
      const posDegLocal = (v: number) => {
        const t = (v - vLo) / range;
        const t2 = t * t;
        const t3 = t2 * t;
        const h10 = t3 - 2 * t2 + t;
        const h01 = -2 * t3 + 3 * t2;
        const h11 = t3 - t2;
        return tStart + h10 * range * dpuA + h01 * transArc + h11 * range * dpuB;
      };
      const valueAtLocal = (targetDeg: number) => {
        let lo = vLo;
        let hi = vHi;
        for (let j = 0; j < 60; j++) {
          const mid = (lo + hi) * 0.5;
          if (posDegLocal(mid) < targetDeg) lo = mid;
          else hi = mid;
        }
        return (lo + hi) * 0.5;
      };
      segments.push({
        kind: 'cubic',
        vLo,
        vHi,
        startDeg: tStart,
        endDeg: tStart + transArc,
        posDeg: posDegLocal,
        valueAt: valueAtLocal,
        // Regime A's majorStep — used by `visibleTicks` to emit
        // the major(s) that fall inside the transition at their
        // cubic-evaluated arc positions.
        majorStep: r.majorStep,
      });
      cursorDeg = tStart + transArc;
      }
    }
  }

  const firstSeg = segments[0];
  const lastSeg = segments[segments.length - 1];
  const lastRegime = sorted[sorted.length - 1];
  const lastDpu = dpuFor(lastRegime);

  function posDeg(v: number): number {
    const clamped = Math.max(0, v);
    if (clamped <= firstSeg.vLo) return firstSeg.startDeg;
    for (const s of segments) {
      if (clamped <= s.vHi) return s.posDeg(clamped);
    }
    // Past the last regime — extrapolate linearly with its dpu so
    // `posDeg` stays monotonic even for values outside the declared
    // range. Happens for extreme altitudes / speeds we didn't cap.
    return lastSeg.endDeg + (clamped - lastSeg.vHi) * lastDpu;
  }

  function valueAt(deg: number): number {
    if (deg <= firstSeg.startDeg) return firstSeg.vLo;
    for (const s of segments) {
      if (deg <= s.endDeg) return s.valueAt(deg);
    }
    return lastSeg.vHi + (deg - lastSeg.endDeg) / lastDpu;
  }

  function visibleTicks(value: number, halfArcDeg: number): Tick[] {
    const cursorArc = posDeg(Math.max(0, value));
    const loArc = cursorArc - halfArcDeg;
    const hiArc = cursorArc + halfArcDeg;
    const ticks: Tick[] = [];
    for (const s of segments) {
      if (s.endDeg < loArc || s.startDeg > hiArc) continue;
      if (s.kind === 'linear') {
        const visLoV = s.valueAt(Math.max(loArc, s.startDeg));
        const visHiV = s.valueAt(Math.min(hiArc, s.endDeg));
        const minorStep = s.minorStep!;
        const majorStep = s.majorStep!;
        const first = Math.ceil(visLoV / minorStep) * minorStep;
        for (let v = first; v <= visHiV; v += minorStep) {
          const majorFrac = v / majorStep;
          const major = Math.abs(majorFrac - Math.round(majorFrac)) < 1e-9;
          ticks.push({
            value: v,
            deltaDeg: s.posDeg(v) - cursorArc,
            label: formatLabel(v),
            major,
          });
        }
      } else if (s.majorStep) {
        // Emit regime A's majors that fall *strictly inside* the
        // transition (the endpoints are covered by the adjacent
        // linear segments). Each tick sits at its cubic-evaluated
        // position, which is slightly shifted from where a pure
        // regime-A linear grid would put it.
        const majorStep = s.majorStep;
        const first = (Math.floor(s.vLo / majorStep) + 1) * majorStep;
        for (let v = first; v < s.vHi - majorStep * 1e-9; v += majorStep) {
          const deltaDeg = s.posDeg(v) - cursorArc;
          if (Math.abs(deltaDeg) > halfArcDeg) continue;
          ticks.push({
            value: v,
            deltaDeg,
            label: formatLabel(v),
            major: true,
          });
        }
      }
    }
    return ticks;
  }

  return { posDeg, valueAt, formatLabel, visibleTicks };
}

// Decade-aligned regimes: `[0, 10^n]`, `[10^n, 10^(n+1)]`, … The
// regimes touch — the auto-extension logic absorbs one major of
// regime A into the cubic transition so the deceleration stays
// monotonic; the absorbed regime-A major (e.g. `9k`) disappears, but
// the regime-B boundary value (`10k`) is still the first major of
// regime B, so the visible "missing" ticks are the ones *inside* the
// transition zone, not the decade landmarks.
export const SPEED_SCALE = buildScale(
  [
    { vLo: 0, vHi: 10_000, minorStep: 100, majorStep: 1000 },
    { vLo: 10_000, vHi: 100_000, minorStep: 1000, majorStep: 10_000 },
    { vLo: 100_000, vHi: 1_000_000, minorStep: 10_000, majorStep: 100_000 },
    { vLo: 1_000_000, vHi: 10_000_000, minorStep: 100_000, majorStep: 1_000_000 },
    { vLo: 10_000_000, vHi: 100_000_000, minorStep: 1_000_000, majorStep: 10_000_000 },
  ],
  formatSpeedLabel,
);

export const ALTITUDE_SCALE = buildScale(
  [
    { vLo: 0, vHi: 10_000, minorStep: 500, majorStep: 1000 },
    { vLo: 10_000, vHi: 100_000, minorStep: 1000, majorStep: 10_000 },
    { vLo: 100_000, vHi: 1_000_000, minorStep: 10_000, majorStep: 100_000 },
    { vLo: 1_000_000, vHi: 10_000_000, minorStep: 100_000, majorStep: 1_000_000 },
    { vLo: 10_000_000, vHi: 100_000_000, minorStep: 1_000_000, majorStep: 10_000_000 },
    { vLo: 100_000_000, vHi: 1_000_000_000, minorStep: 10_000_000, majorStep: 100_000_000 },
    { vLo: 1_000_000_000, vHi: 10_000_000_000, minorStep: 100_000_000, majorStep: 1_000_000_000 },
    { vLo: 10_000_000_000, vHi: 100_000_000_000, minorStep: 1_000_000_000, majorStep: 10_000_000_000 },
    { vLo: 100_000_000_000, vHi: 1_000_000_000_000, minorStep: 10_000_000_000, majorStep: 100_000_000_000 },
  ],
  formatAltLabel,
);

// Radial layout constants (SVG coords).
export const CURVED_TAPE_SIZE = 488;
export const CURVED_PANEL_INNER_R = 133;
export const CURVED_PANEL_OUTER_R = 212;
export const CURVED_TICK_INNER_R = 133;
export const CURVED_TICK_MINOR_R = 142;
export const CURVED_TICK_MAJOR_R = 150;
export const CURVED_LABEL_R = 153;
export const CURVED_VISIBLE_HALF_ARC = 48;

export function buildPanelPath(baseAngleDeg: number): string {
  const halfRad = (CURVED_VISIBLE_HALF_ARC * Math.PI) / 180;
  const baseRad = (baseAngleDeg * Math.PI) / 180;
  const lo = baseRad - halfRad;
  const hi = baseRad + halfRad;
  const rIn = CURVED_PANEL_INNER_R;
  const rOut = CURVED_PANEL_OUTER_R;
  const p = (n: number) => n.toFixed(2);
  return (
    `M ${p(Math.cos(lo) * rIn)} ${p(Math.sin(lo) * rIn)}` +
    ` A ${rIn} ${rIn} 0 0 1 ${p(Math.cos(hi) * rIn)} ${p(Math.sin(hi) * rIn)}` +
    ` L ${p(Math.cos(hi) * rOut)} ${p(Math.sin(hi) * rOut)}` +
    ` A ${rOut} ${rOut} 0 0 0 ${p(Math.cos(lo) * rOut)} ${p(Math.sin(lo) * rOut)}` +
    ' Z'
  );
}

export const PANEL_PATH_LEFT = buildPanelPath(180);
export const PANEL_PATH_RIGHT = buildPanelPath(0);
