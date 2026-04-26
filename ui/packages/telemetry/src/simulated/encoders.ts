// Encoders that turn the decoded `*Data` types back into the wire
// tuples the live broadcaster sends. Used by `SimulatedKsp` so its
// dispatch path produces the same shape consumers see in production
// — hooks decode wire-shaped frames whether they came off the
// websocket or out of a fixture. Mirror inverses of the decoders in
// `../dragonglass/decoders.ts`; keep the two files in lockstep.

import type {
  ClockData,
} from '../core/clock-data';
import type { GameData } from '../core/game-data';
import type { EditorStateData } from '../core/editor-state-data';
import type { FlightData } from '../core/flight-data';
import type { EngineData } from '../core/engine-data';
import type { StageData } from '../core/stage-data';
import type {
  PartData,
  PartResourceData,
  PartModuleData,
  PawEvent,
} from '../core/part-data';
import type { PartCatalogData, PartCatalogEntry } from '../core/part-catalog-data';
import { CATEGORY_BY_INDEX } from '../core/part-catalog-data';
import type {
  ClockWire,
  GameWire,
  EditorStateWire,
  FlightWire,
  EngineWire,
  EnginePointWire,
  EnginePropellantWire,
  StageWire,
  StageEntryWire,
  StagingPartWire,
  PawWire,
  PartWire,
  PartResourceWire,
  PartModuleWire,
  PartCatalogWire,
  PartCatalogEntryWire,
} from '../core/wire';

export function encodeClock(d: ClockData): ClockWire {
  return [d.ut, d.met];
}

export function encodeGame(d: GameData): GameWire {
  return [d.scene, d.activeVesselId, d.timewarp, d.mapActive];
}

export function encodeEditorState(d: EditorStateData): EditorStateWire {
  return [d.heldPart];
}

const SPEED_DISPLAY_MODE_INDEX: Record<string, 0 | 1 | 2> = {
  orbit: 0,
  surface: 1,
  target: 2,
};

export function encodeFlight(d: FlightData): FlightWire {
  return [
    d.vesselId,
    d.altitudeAsl,
    d.altitudeRadar,
    [d.surfaceVelocity.x, d.surfaceVelocity.y, d.surfaceVelocity.z],
    [d.orbitalVelocity.x, d.orbitalVelocity.y, d.orbitalVelocity.z],
    d.throttle,
    d.sas,
    d.rcs,
    [d.orientation.x, d.orientation.y, d.orientation.z, d.orientation.w],
    [d.angularVelocity.x, d.angularVelocity.y, d.angularVelocity.z],
    d.hasTarget
      ? [d.targetVelocity.x, d.targetVelocity.y, d.targetVelocity.z]
      : null,
    d.deltaVMission,
    d.currentThrust,
    d.stageIdx,
    d.deltaVStage,
    d.twrStage,
    SPEED_DISPLAY_MODE_INDEX[d.speedDisplayMode] ?? 1,
  ];
}

const ENGINE_STATUS_INDEX: Record<string, 0 | 1 | 2 | 3 | 4> = {
  burning: 0,
  flameout: 1,
  failed: 2,
  shutdown: 3,
  idle: 4,
};

export function encodeEngines(d: EngineData): EngineWire {
  const points: EnginePointWire[] = d.engines.map((e) => {
    const props: EnginePropellantWire[] = e.propellants.map((p) => [
      p.resourceName,
      p.abbr,
      p.available,
      p.capacity,
    ]);
    return [
      e.id,
      e.x,
      e.y,
      ENGINE_STATUS_INDEX[e.status] ?? 3,
      e.throttle,
      e.maxThrust,
      e.isp,
      e.crossfeedPartIds.slice(),
      props,
    ];
  });
  return [d.vesselId, points];
}

export function encodeStage(d: StageData): StageWire {
  const stages: StageEntryWire[] = d.stages.map((s) => {
    const parts: StagingPartWire[] = s.parts.map((p) => [
      p.kind,
      p.persistentId,
      p.iconName,
      p.cousinsInStage.slice(),
    ]);
    return [s.stageNum, s.deltaVActual, s.twrActual, parts];
  });
  return [d.vesselId, d.currentStageIdx, stages];
}

export function encodePaw(ev: PawEvent): PawWire {
  return [ev.persistentId];
}

function encodeResource(r: PartResourceData): PartResourceWire {
  return [r.name, r.abbr, r.available, r.capacity];
}

function encodeModule(m: PartModuleData): PartModuleWire {
  switch (m.kind) {
    case 'engines':
      return [
        'E',
        m.moduleName,
        m.status,
        m.thrustLimit,
        m.currentThrust,
        m.maxThrust,
        m.realIsp,
        m.propellants.map((p) => [
          p.name,
          p.displayName,
          p.ratio,
          p.currentAmount,
          p.totalAvailable,
        ]),
      ];
    case 'sensor':
      return [
        'S',
        m.moduleName,
        m.sensorType,
        m.active,
        m.value,
        m.unit,
        m.statusText,
      ];
    case 'science':
      return [
        'X',
        m.moduleName,
        m.experimentTitle,
        m.state,
        m.rerunnable,
        m.transmitValue,
        m.dataAmount,
      ];
    case 'solar':
      return [
        'V',
        m.moduleName,
        m.state,
        m.flowRate,
        m.chargeRate,
        m.sunAOA,
        m.retractable,
        m.isTracking,
      ];
    case 'generator':
      return [
        'R',
        m.moduleName,
        m.active,
        m.alwaysOn,
        m.efficiency,
        m.status,
        m.inputs.map((f) => [f.name, f.rate]),
        m.outputs.map((f) => [f.name, f.rate]),
      ];
    case 'light':
      return ['L', m.moduleName, m.on, m.r, m.g, m.b];
    case 'parachute':
      return [
        'C',
        m.moduleName,
        m.state,
        m.safeState,
        m.deployAltitude,
        m.minPressure,
      ];
    case 'command':
      return [
        'M',
        m.moduleName,
        m.crewCount,
        m.minimumCrew,
        m.controlState,
        m.hibernate,
        m.hibernateOnWarp,
      ];
    case 'reactionWheel':
      return [
        'W',
        m.moduleName,
        m.state,
        m.authorityLimiter,
        m.pitchTorque,
        m.yawTorque,
        m.rollTorque,
        m.actuatorMode,
      ];
    case 'rcs':
      return [
        'T',
        m.moduleName,
        m.enabled,
        m.thrustLimit,
        m.thrusterPower,
        m.realIsp,
        m.propellants.map((p) => [
          p.name,
          p.displayName,
          p.ratio,
          p.currentAmount,
          p.totalAvailable,
        ]),
      ];
    case 'decoupler':
      return [
        'D',
        m.moduleName,
        m.isDecoupled,
        m.isAnchored,
        m.ejectionForce,
      ];
    case 'transmitter':
      return [
        'A',
        m.moduleName,
        m.antennaType,
        m.antennaPower,
        m.packetSize,
        m.packetInterval,
        m.busy,
      ];
    case 'deployAntenna':
      return ['Y', m.moduleName, m.state, m.retractable];
    case 'deployRadiator':
      return ['Z', m.moduleName, m.state, m.retractable];
    case 'activeRadiator':
      return ['K', m.moduleName, m.isCooling, m.maxTransfer, m.status];
    case 'harvester':
      return [
        'J',
        m.moduleName,
        m.active,
        m.status,
        m.resourceName,
        m.harvesterType,
        m.abundance,
        m.thermalEfficiency,
        m.loadCapacity,
      ];
    case 'converter':
      return [
        'U',
        m.moduleName,
        m.active,
        m.converterName,
        m.status,
        m.inputs.map((f) => [f.name, f.rate]),
        m.outputs.map((f) => [f.name, f.rate]),
      ];
    case 'controlSurface':
      return [
        'F',
        m.moduleName,
        m.ignorePitch,
        m.ignoreYaw,
        m.ignoreRoll,
        m.authorityLimiter,
        m.deploy,
        m.deployInvert,
        m.deployAngle,
      ];
    case 'alternator':
      return [
        'N',
        m.moduleName,
        m.outputRate,
        m.outputName,
        m.outputUnits,
        m.engineRunning,
      ];
    case 'generic':
    default: {
      // Generic falls through to the wire-format slot used by the
      // server's catch-all kind 'G': [G, moduleName, events, fields].
      const generic = m as PartModuleData & {
        events?: { name: string; guiName: string }[];
        fields?: unknown[];
      };
      const events = (generic.events ?? []).map((e) => [e.name, e.guiName]);
      const fields = (generic.fields ?? []).slice();
      return ['G', m.moduleName, events, fields];
    }
  }
}

export function encodePart(d: PartData): PartWire {
  if (d.gone) return [] as unknown as PartWire;
  // Live transport divides by devicePixelRatio; the inverse multiplies
  // back so a round-trip through encode→decode lands on the same x/y
  // the simulated transport produced. Sim has no real devicePixelRatio
  // dependency, but we mirror exactly so the contract holds.
  const dpr = typeof window !== 'undefined' && window.devicePixelRatio > 0
    ? window.devicePixelRatio
    : 1;
  const screen: [number, number, boolean] = d.screen
    ? [d.screen.x * dpr, d.screen.y * dpr, d.screen.visible]
    : [0, 0, false];
  return [
    d.persistentId,
    d.name,
    screen,
    d.resources.map(encodeResource),
    d.modules.map(encodeModule),
    d.distanceFromActiveM,
  ];
}

const CATEGORY_INDEX: Record<string, number> = (() => {
  const out: Record<string, number> = {};
  for (let i = 0; i < CATEGORY_BY_INDEX.length; i++) {
    out[CATEGORY_BY_INDEX[i]] = i;
  }
  return out;
})();

export function encodePartCatalog(d: PartCatalogData): PartCatalogWire {
  return d.entries.map((e: PartCatalogEntry): PartCatalogEntryWire => [
    e.name,
    e.title,
    CATEGORY_INDEX[e.category] ?? 0,
    e.manufacturer,
    e.cost,
    e.mass,
    e.description,
    e.techRequired,
    e.tags,
    e.iconBase64,
    e.bulkheadProfiles,
  ]);
}
