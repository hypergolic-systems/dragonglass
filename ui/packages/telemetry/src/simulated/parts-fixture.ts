// Fixture for PAW simulation. A handful of fake parts, each with a
// nominal viewport position (points on a slow Lissajous so the leader
// lines visibly drift) and a static resource loadout that the
// simulated provider drains over time.

import type { PartResourceData, PartModuleData } from '../core/part-data';

export interface SimulatedPart {
  readonly persistentId: string;
  readonly name: string;
  /** Base normalized position in [-1, 1] around viewport centre. */
  readonly baseX: number;
  readonly baseY: number;
  /** Lissajous amplitudes, normalized. */
  readonly ampX: number;
  readonly ampY: number;
  /** Phase offset so the parts don't all drift in lockstep. */
  readonly phase: number;
  /** Immutable template the drain sim scales `available` against. */
  readonly resources: readonly PartResourceData[];
  /** PartModules attached to this part. Events fire no-op handlers
   *  in the simulator; labels are static strings. */
  readonly modules: readonly PartModuleData[];
}

export const SIMULATED_PARTS: readonly SimulatedPart[] = [
  {
    persistentId: '4001',
    name: "RT-10 'Hammer' Solid Fuel Booster",
    baseX: -0.42,
    baseY: 0.08,
    ampX: 0.06,
    ampY: 0.04,
    phase: 0,
    resources: [
      { name: 'SolidFuel', abbr: 'SF', available: 375, capacity: 375, flow: -4.8 },
    ],
    modules: [
      {
        kind: 'engines',
        moduleName: 'ModuleEnginesFX',
        status: 'burning',
        thrustLimit: 100,
        currentThrust: 227,
        maxThrust: 227,
        realIsp: 195,
        propellants: [
          {
            name: 'SolidFuel',
            displayName: 'Solid Fuel',
            ratio: 1,
            currentAmount: 4.8,
            totalAvailable: 375,
          },
        ],
      },
    ],
  },
  {
    persistentId: '4002',
    name: 'FL-T800 Fuel Tank',
    baseX: 0.18,
    baseY: -0.14,
    ampX: 0.08,
    ampY: 0.05,
    phase: 1.7,
    resources: [
      { name: 'LiquidFuel', abbr: 'LF', available: 360, capacity: 360, flow: -2.2 },
      { name: 'Oxidizer', abbr: 'OX', available: 440, capacity: 440, flow: -2.7 },
    ],
    modules: [],
  },
  {
    persistentId: '4003',
    name: 'Z-400 Rechargeable Battery',
    baseX: 0.36,
    baseY: 0.26,
    ampX: 0.05,
    ampY: 0.03,
    phase: 3.4,
    resources: [
      { name: 'ElectricCharge', abbr: 'EC', available: 280, capacity: 400, flow: +1.2 },
    ],
    modules: [],
  },
  {
    persistentId: '4005',
    name: '2HOT Thermometer',
    baseX: 0.3,
    baseY: -0.05,
    ampX: 0.03,
    ampY: 0.02,
    phase: 2.4,
    resources: [],
    modules: [
      {
        kind: 'sensor',
        moduleName: 'ModuleEnviroSensor',
        sensorType: 'temperature',
        active: true,
        value: 287.42,
        unit: 'K',
        statusText: 'Active',
      },
    ],
  },
  {
    persistentId: '4006',
    name: 'SP-W 3x2 Photovoltaic Panels',
    baseX: -0.28,
    baseY: 0.22,
    ampX: 0.04,
    ampY: 0.03,
    phase: 0.7,
    resources: [],
    modules: [
      {
        kind: 'solar',
        moduleName: 'ModuleDeployableSolarPanel',
        state: 'extended',
        flowRate: 2.84,
        chargeRate: 3.5,
        sunAOA: 0.81,
        retractable: true,
        isTracking: true,
      },
    ],
  },
  {
    persistentId: '4007',
    name: 'PresMat Barometer',
    baseX: 0.48,
    baseY: 0.04,
    ampX: 0.03,
    ampY: 0.02,
    phase: 4.1,
    resources: [],
    modules: [
      {
        kind: 'science',
        moduleName: 'ModuleScienceExperiment',
        experimentTitle: 'Barometric Pressure',
        state: 'ready',
        rerunnable: true,
        transmitValue: 3.2,
        dataAmount: 0.8,
      },
    ],
  },
  {
    persistentId: '4008',
    name: 'PB-NUK Radioisotope Generator',
    baseX: -0.38,
    baseY: -0.22,
    ampX: 0.03,
    ampY: 0.02,
    phase: 2.2,
    resources: [],
    modules: [
      {
        kind: 'generator',
        moduleName: 'ModuleGenerator',
        active: true,
        alwaysOn: true,
        efficiency: 1,
        status: 'Nominal',
        inputs: [],
        outputs: [{ name: 'ElectricCharge', rate: 0.75 }],
      },
    ],
  },
  {
    persistentId: '4009',
    name: 'Illuminator Mk1',
    baseX: 0.08,
    baseY: 0.34,
    ampX: 0.03,
    ampY: 0.02,
    phase: 1.1,
    resources: [],
    modules: [
      {
        kind: 'light',
        moduleName: 'ModuleLight',
        on: true,
        r: 1,
        g: 0.92,
        b: 0.72,
      },
    ],
  },
  {
    persistentId: '4010',
    name: "Mk16 Parachute",
    baseX: -0.05,
    baseY: 0.44,
    ampX: 0.04,
    ampY: 0.02,
    phase: 3.9,
    resources: [],
    modules: [
      {
        kind: 'parachute',
        moduleName: 'ModuleParachute',
        state: 'stowed',
        safeState: 'safe',
        deployAltitude: 1000,
        minPressure: 0.04,
      },
    ],
  },
  {
    persistentId: '4004',
    name: 'Mk1 Command Pod',
    baseX: -0.1,
    baseY: -0.32,
    ampX: 0.04,
    ampY: 0.03,
    phase: 5.1,
    resources: [
      { name: 'ElectricCharge', abbr: 'EC', available: 42, capacity: 50, flow: -0.08 },
      { name: 'MonoPropellant', abbr: 'MP', available: 7.5, capacity: 10, flow: 0 },
    ],
    modules: [
      {
        kind: 'generic',
        moduleName: 'ModuleCommand',
        events: [
          { id: 'MakeReference', label: 'Control From Here' },
          { id: 'RenameVessel', label: 'Rename Vessel' },
        ],
        fields: [
          { kind: 'label', id: 'crew', label: 'Crew', value: '1 / 1' },
          {
            kind: 'toggle',
            id: 'hibernate',
            label: 'Hibernation',
            value: false,
            enabledText: 'Disabled',
            disabledText: 'Enabled',
          },
          {
            kind: 'option',
            id: 'commScheme',
            label: 'Comm Band',
            selectedIndex: 1,
            display: ['S-Band', 'X-Band', 'Ka-Band'],
          },
        ],
      },
      {
        kind: 'generic',
        moduleName: 'ModuleReactionWheel',
        events: [
          { id: 'OnToggle', label: 'Toggle Torque' },
        ],
        fields: [
          {
            kind: 'toggle',
            id: 'active',
            label: 'Reaction Wheel',
            value: true,
            enabledText: 'Active',
            disabledText: 'Inactive',
          },
          {
            kind: 'numeric',
            id: 'authLimit',
            label: 'Authority Limit',
            value: 100,
            min: 0,
            max: 100,
            incLarge: 10,
            incSmall: 1,
            incSlide: 0.5,
            unit: '%',
          },
        ],
      },
    ],
  },
];
