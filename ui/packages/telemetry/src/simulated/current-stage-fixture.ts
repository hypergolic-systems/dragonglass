// Simulated current-stage loadout for dev-mode (`just ui-dev`).
// Mirrors the shape the live `currentStage` topic produces on
// the C# side.
//
// Profile matches `engines-fixture.ts`: 1 × 1200 kN central engine
// plus 2 × 500 kN side boosters. The central engine and the side
// boosters run on independent tank sets — a realistic booster
// configuration — so they resolve to two distinct fuel groups.
// That exercises the multi-group path in the UI: two fuel-row
// icons side-by-side, each highlighting its members against the
// others, each with its own LFO gauge at its own fill level.
//
// Tanks start FULL; `SimulatedKsp` applies per-tick drain so the
// gauges walk through nominal → warn → alert over a ~45-s loop.

import type { CurrentStageData } from '../core/current-stage-data';

export const CURRENT_STAGE: CurrentStageData = {
  stageIdx: 1,
  deltaVStage: 2100,
  twrStage: 1.3,
  groups: [
    {
      // Central engine — bigger tanks, drains slower (reaches
      // empty at ~45 s). Always the deeper-burning stage in this
      // split configuration.
      engineIds: ['eng-center'],
      propellants: [
        { resourceName: 'LiquidFuel', available: 900, capacity: 900 },
        { resourceName: 'Oxidizer', available: 1100, capacity: 1100 },
      ],
    },
    {
      // Side boosters — smaller tanks, drains faster (reaches
      // empty at ~30 s). First to cross the warn and alert
      // thresholds, matching how real stage-1 boosters deplete
      // ahead of the core.
      engineIds: ['eng-left', 'eng-right'],
      propellants: [
        { resourceName: 'LiquidFuel', available: 400, capacity: 400 },
        { resourceName: 'Oxidizer', available: 500, capacity: 500 },
      ],
    },
  ],
};

/** Per-group drain durations, seconds. `SimulatedKsp` uses these
 *  to linearly walk each group's `available` from capacity to
 *  zero over a looping cycle so the UI can be observed
 *  transitioning through the gauge-state thresholds. */
export const CURRENT_STAGE_DRAIN_SECONDS = [45, 30];

/** Cycle length — once every group has drained to 0, we reset
 *  back to full. Slightly longer than the slowest group so the
 *  alert state is visible for a beat before the refill. */
export const CURRENT_STAGE_CYCLE_SECONDS = 50;
