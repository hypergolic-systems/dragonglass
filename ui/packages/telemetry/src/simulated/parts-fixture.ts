// Fixture for PAW simulation. A handful of fake parts, each with a
// nominal viewport position (points on a slow Lissajous so the leader
// lines visibly drift) and a static resource loadout that the
// simulated provider drains over time.

import type { PartResourceData } from '../core/part-data';

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
  },
];
