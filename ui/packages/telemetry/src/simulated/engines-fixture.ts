// Simulated engine loadout for dev-mode (`just ui-dev`). Mirrors the
// shape produced by the `engines` topic in the live KSP plugin.
//
// Vessel profile: a small booster stack — one powerful central engine
// flanked by two smaller side boosters sharing a single crossfeed
// cluster. Positions are in body-local XZ metres (matching the live
// wire format: +X starboard, +Z forward), so the UI's engine map
// draws them on a horizontal line with the central bell in the
// middle.
//
// Two crossfeed clusters:
//   - center engine pulls from a private LF/OX stack (tanks
//     "t-core-a", "t-core-b"),
//   - left + right side boosters share a common LF/OX cluster
//     (tanks "t-side-a", "t-side-b").
// The UI's grouping pass sees identical crossfeedPartIds +
// propellant sets on the two side boosters and pools them into one
// fuel group; the centre engine lands in its own group. That's the
// two-row configuration the Propulsion panel renders in dev mode.
//
// Drain in `SimulatedKsp` is keyed by `clusterId`: the core cluster
// drains slower than the side cluster, so the side boosters cross
// the fuel warn/alert thresholds first — matching how real stage-1
// boosters deplete ahead of the core.

import type { EngineData, EnginePoint } from '../core/engine-data';

/**
 * Extends `EnginePoint` with the sim-only fields used to drive the
 * drain cycle. The live wire carries only `EnginePoint` values.
 */
export interface SimEnginePoint extends EnginePoint {
  readonly clusterId: string;
}

const CORE_TANKS: readonly string[] = ['t-core-a', 't-core-b'];
const SIDE_TANKS: readonly string[] = ['t-side-a', 't-side-b'];

export const ENGINES_SIM: SimEnginePoint[] = [
  {
    id: 'eng-center',
    x: 0.0,
    y: 0.0,
    status: 'burning',
    throttle: 1,
    maxThrust: 1200,
    isp: 310,
    crossfeedPartIds: CORE_TANKS,
    propellants: [
      { resourceName: 'LiquidFuel', abbr: 'LF', available: 900, capacity: 900 },
      { resourceName: 'Oxidizer', abbr: 'Ox', available: 1100, capacity: 1100 },
    ],
    clusterId: 'core',
  },
  {
    id: 'eng-left',
    x: -1.5,
    y: 0.0,
    status: 'burning',
    throttle: 1,
    maxThrust: 500,
    isp: 285,
    crossfeedPartIds: SIDE_TANKS,
    propellants: [
      { resourceName: 'LiquidFuel', abbr: 'LF', available: 400, capacity: 400 },
      { resourceName: 'Oxidizer', abbr: 'Ox', available: 500, capacity: 500 },
    ],
    clusterId: 'side',
  },
  {
    id: 'eng-right',
    x: 1.5,
    y: 0.0,
    status: 'burning',
    throttle: 1,
    maxThrust: 500,
    isp: 285,
    crossfeedPartIds: SIDE_TANKS,
    propellants: [
      { resourceName: 'LiquidFuel', abbr: 'LF', available: 400, capacity: 400 },
      { resourceName: 'Oxidizer', abbr: 'Ox', available: 500, capacity: 500 },
    ],
    clusterId: 'side',
  },
];

export const ENGINES: EngineData = {
  vesselId: 'sim-vessel',
  engines: ENGINES_SIM,
};

/** Summed max thrust across the simulated loadout, kN. Used by the
 *  flight sim to scale `currentThrust` from the current throttle. */
export const ENGINES_MAX_THRUST = ENGINES_SIM.reduce(
  (s, e) => s + e.maxThrust,
  0,
);

/** Per-cluster drain duration, seconds. Core drains slower than the
 *  sides so the UI walks through warn/alert on the side boosters
 *  first, matching real-world booster-first depletion. */
export const CLUSTER_DRAIN_SECONDS: Record<string, number> = {
  core: 45,
  side: 30,
};

/** Cycle length, seconds. Slightly longer than the slowest cluster
 *  so the alert state is visible for a beat before the refill. */
export const ENGINES_CYCLE_SECONDS = 50;

/** Sim-mode stage scalars — stable per run. Chosen to match the
 *  fuel/thrust profile above plausibly. Live mode pulls these from
 *  stock `VesselDeltaV.GetStage(...)`. */
export const STAGE_IDX_SIM = 1;
export const STAGE_DELTA_V_SIM = 2100;
export const STAGE_TWR_SIM = 1.3;
