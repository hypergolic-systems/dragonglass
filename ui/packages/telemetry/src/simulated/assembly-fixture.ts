import type { AssemblyModel } from '../core/assembly';

export const ASSEMBLY: AssemblyModel = {
  name: 'KOE-1 ORBITAL COMPLEX',
  homeId: 'station-alpha',
  vessels: [
    {
      id: 'station-alpha',
      name: 'STATION ALPHA',
      callsign: 'KOE-1',
      role: 'home',
      dryMass: 12.0,
      ports: [
        { id: 'fwd1', name: 'FWD · 1' },
        {
          id: 'fwd2',
          name: 'FWD · 2',
          connectedTo: { vesselId: 'sci-shuttle', portId: 'fwd' },
        },
        { id: 'aft1', name: 'AFT · 1' },
      ],
      parts: [
        {
          id: 's-gig-a',
          name: 'GIGANTOR XL ARRAY · A',
          kind: 'solar',
          ecFlow: +8.54,
          status: 'DEPLOYED',
          sub: 'sun 0.35',
        },
        {
          id: 's-gig-b',
          name: 'GIGANTOR XL ARRAY · B',
          kind: 'solar',
          ecFlow: +8.54,
          status: 'DEPLOYED',
          sub: 'sun 0.35',
        },
        {
          id: 's-rtg',
          name: 'PB-NUK RTG',
          kind: 'rtg',
          ecFlow: +0.75,
          status: 'NOMINAL',
        },
        {
          id: 's-z4k',
          name: 'Z-4K BATTERY BANK',
          kind: 'battery',
          ecStorage: { current: 3140, capacity: 4000 },
        },
        {
          id: 's-cupola',
          name: 'PPD-12 CUPOLA',
          kind: 'pod',
          ecFlow: -0.06,
          status: 'CREW 1',
        },
        {
          id: 's-ra2',
          name: 'RA-2 RELAY ANTENNA',
          kind: 'antenna',
          ecFlow: -0.03,
          status: 'IDLE',
        },
      ],
    },
    {
      id: 'sci-shuttle',
      name: 'SCIENCE SHUTTLE',
      callsign: 'SCI-07',
      role: 'visitor',
      dryMass: 3.65,
      ports: [
        {
          id: 'fwd',
          name: 'CLAMP-O-TRON FWD',
          connectedTo: { vesselId: 'station-alpha', portId: 'fwd2' },
        },
      ],
      parts: [
        {
          id: 'v-pod',
          name: 'MK1-3 COMMAND POD',
          kind: 'pod',
          ecFlow: -0.4,
          status: 'CREW 2',
          tanks: {
            Mono: { current: 30, capacity: 30 },
          },
          sasTorque: 8,
        },
        {
          id: 'v-goo',
          name: 'MYSTERY GOO · CONTAINER',
          kind: 'science',
          ecFlow: -0.8,
          status: 'RUNNING',
          sub: 'biome · low orbit',
        },
        {
          id: 'v-rcs',
          name: 'RV-105 RCS BLOCK',
          kind: 'rcs',
          status: 'IDLE',
          engine: {
            thrust: 4.0,
            ispVac: 240,
            ispAtm: 100,
            propellants: ['Mono'],
          },
        },
        {
          id: 'v-fl400',
          name: 'FL-T400 LF/OX TANK',
          kind: 'tank',
          status: 'FULL',
          tanks: {
            LF: { current: 220, capacity: 220 },
            Ox: { current: 270, capacity: 270 },
          },
        },
        {
          id: 'v-lv909',
          name: 'LV-909 TERRIER',
          kind: 'engine',
          status: 'SHUTDOWN',
          engine: {
            thrust: 60,
            ispVac: 345,
            ispAtm: 85,
            propellants: ['LF', 'Ox'],
          },
        },
        {
          id: 'v-z200',
          name: 'Z-200 BATTERY',
          kind: 'battery',
          ecStorage: { current: 180, capacity: 200 },
        },
      ],
    },
  ],
};
