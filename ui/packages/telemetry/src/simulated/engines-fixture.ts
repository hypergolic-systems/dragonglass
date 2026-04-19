// Simulated engine loadout for dev-mode (`just ui-dev`). Mirrors the
// shape produced by the `engines` topic in the live KSP plugin.
//
// Vessel profile: a small booster stack — one powerful central engine
// flanked by two smaller side boosters. Positions are in body-local
// XZ metres (matching the live wire format: +X starboard, +Z forward),
// so the UI's engine map draws them on a horizontal line with the
// central bell in the middle.

import type { EngineData } from '../core/engine-data';

export const ENGINES: EngineData = {
  vesselId: 'sim-vessel',
  engines: [
    { id: 'eng-center', x: 0.0, y: 0.0, status: 'burning', maxThrust: 1200 },
    { id: 'eng-left', x: -1.5, y: 0.0, status: 'burning', maxThrust: 500 },
    { id: 'eng-right', x: 1.5, y: 0.0, status: 'burning', maxThrust: 500 },
  ],
};

/** Summed max thrust across the simulated loadout, kN. Used by the
 *  flight sim to scale `currentThrust` from the current throttle. */
export const ENGINES_MAX_THRUST = ENGINES.engines.reduce(
  (s, e) => s + e.maxThrust,
  0,
);
