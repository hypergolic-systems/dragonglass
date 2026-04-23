// Simulated StageTopic fixture. A static three-stage stack so the
// dev-mode StagingStack has something to render without a live KSP
// feed. Mirrors stock's OperatingStageInfo ordering (lower stageNum
// = later) and matches the real wire shape bit-for-bit so the UI
// can't tell the two apart.

import type { StageData, StageEntry } from '../core/stage-data';

const s2: StageEntry = {
  stageNum: 2,
  deltaVActual: 820,
  twrActual: 1.55,
  parts: [
    {
      kind: 'engine',
      persistentId: '5001',
      iconName: 'SOLID_BOOSTER',
      cousinsInStage: ['5002', '5003', '5004'],
    },
    {
      kind: 'decoupler',
      persistentId: '5011',
      iconName: 'DECOUPLER_VERT',
      cousinsInStage: ['5012', '5013', '5014'],
    },
    {
      kind: 'clamp',
      persistentId: '5020',
      iconName: 'FUEL_DUCT',
      cousinsInStage: [],
    },
  ],
};

const s1: StageEntry = {
  stageNum: 1,
  deltaVActual: 2100,
  twrActual: 1.22,
  parts: [
    {
      kind: 'engine',
      persistentId: '5100',
      iconName: 'LIQUID_ENGINE',
      cousinsInStage: [],
    },
    {
      kind: 'decoupler',
      persistentId: '5110',
      iconName: 'DECOUPLER_VERT',
      cousinsInStage: [],
    },
  ],
};

const s0: StageEntry = {
  stageNum: 0,
  deltaVActual: 880,
  twrActual: 0.58,
  parts: [
    {
      kind: 'parachute',
      persistentId: '5200',
      iconName: 'PARACHUTES',
      cousinsInStage: [],
    },
  ],
};

export const SIM_STAGE_DATA: StageData = {
  vesselId: 'sim-vessel',
  currentStageIdx: 2,
  stages: [s2, s1, s0],
};

export const SIM_STAGE_DATA_EDITOR: StageData = {
  vesselId: 'editor',
  // -1 = no stage is "current" in the editor — matches what the real
  // server emits for ShipConstruct-backed frames.
  currentStageIdx: -1,
  stages: [s2, s1, s0],
};
