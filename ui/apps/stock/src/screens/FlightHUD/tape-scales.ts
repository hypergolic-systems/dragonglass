import { formatAltLabel } from './format';

export interface TickGrid {
  /** Generate ticks from `minV` (inclusive) up to `maxV` (exclusive). */
  minV: number;
  maxV: number;
  minorStep: number;
  majorStep: number;
  formatLabel: (v: number) => string;
}

export interface TapeScale {
  max: number;
  /** Piecewise tick grids, concatenated along the value axis. */
  tickGrids: TickGrid[];
  /**
   * Monotonic "tape arc length from 0" in degrees. Tick position relative
   * to the cursor is `posDeg(tick) - posDeg(cursor)`. A linear `posDeg`
   * gives a uniform tape; a piecewise one blended with smoothstep lets
   * the altitude tape ease from a fine scale (near the ground) into a
   * coarse one (above 10 km) without a hard jump at the boundary.
   */
  posDeg: (v: number) => number;
}

// Linear tape: 0.5° per m/s, 25° between majors.
export const SPEED_SCALE: TapeScale = {
  max: 3000,
  tickGrids: [
    { minV: 0, maxV: 3001, minorStep: 10, majorStep: 50, formatLabel: (v) => String(v) },
  ],
  posDeg: (v) => 0.5 * v,
};

// Altitude tape: fine metres below the transition, coarse km above.
const ALT_DPU_LOW = 0.025;
const ALT_DPU_HIGH = 0.0025;
const ALT_T_LO = 99_000;
const ALT_T_HI = 100_000;
const ALT_TRANSITION_WIDTH = ALT_T_HI - ALT_T_LO;
const ALT_POS_AT_T_LO = ALT_DPU_LOW * ALT_T_LO;
const ALT_POS_AT_T_HI =
  ALT_POS_AT_T_LO + (ALT_TRANSITION_WIDTH * (ALT_DPU_LOW + ALT_DPU_HIGH)) / 2;

function altitudePosDeg(v: number): number {
  if (v <= ALT_T_LO) return ALT_DPU_LOW * v;
  if (v >= ALT_T_HI) return ALT_POS_AT_T_HI + ALT_DPU_HIGH * (v - ALT_T_HI);
  const t = (v - ALT_T_LO) / ALT_TRANSITION_WIDTH;
  const sInt = t ** 3 - (t ** 4) / 2;
  return (
    ALT_POS_AT_T_LO +
    ALT_TRANSITION_WIDTH *
      (ALT_DPU_LOW * t + (ALT_DPU_HIGH - ALT_DPU_LOW) * sInt)
  );
}

export const ALTITUDE_SCALE: TapeScale = {
  max: 200_000,
  tickGrids: [
    {
      minV: 0,
      maxV: 100_000,
      minorStep: 200,
      majorStep: 1000,
      formatLabel: formatAltLabel,
    },
    {
      minV: 100_000,
      maxV: 200_001,
      minorStep: 2000,
      majorStep: 10_000,
      formatLabel: formatAltLabel,
    },
  ],
  posDeg: altitudePosDeg,
};

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
