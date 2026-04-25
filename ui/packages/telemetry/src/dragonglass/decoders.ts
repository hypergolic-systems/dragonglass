// Positional-array decoders for the Dragonglass telemetry wire format.
//
// The server emits each topic as `{"topic":"<name>","data":[...]}` with
// `data` a fixed-shape positional array — no field names on the wire.
// Decoders read positions and populate typed objects.
//
// Each decoder returns a **module-scoped scratch singleton**. The
// subscription hooks copy fields out (`Object.assign` for scalars,
// `.copy()` for three.js refs), so reusing the same instance across
// dispatches avoids per-frame allocation at 10 Hz × 3 topics. Callers
// must not retain the returned reference between callbacks.
//
// The public types (`FlightData`, `EngineData`, `StageData`) are
// `readonly` on every field — that's the consumer-facing contract.
// Internally, the scratch pattern requires mutation, so each decoder
// holds a local `*Mutable` shape (same fields without `readonly`) and
// casts to the public type at the return site. Same bargain the
// consumer-side stores (e.g. `use-flight.svelte.ts`) strike.

import { Quaternion, Vector3 } from 'three';
import type { ClockData } from '../core/clock-data';
import type { ConfigData } from '../core/config-data';
import type { EditorStateData } from '../core/editor-state-data';
import type { GameData } from '../core/game-data';
import type { FlightData, SpeedDisplayMode } from '../core/flight-data';
import type {
  EngineData,
  EnginePoint,
  EngineStatus,
} from '../core/engine-data';
import type {
  StageData,
  StageEntry,
  StagingPart,
  StagingPartKind,
} from '../core/stage-data';
import type {
  PartData,
  PartResourceData,
  PartEventData,
  PartFieldData,
  PartModuleData,
  PartModuleGeneric,
  PartModuleEngines,
  PartEnginePropellant,
  PartEngineStatus,
  PartModuleEnviroSensor,
  EnviroSensorType,
  PartModuleScienceExperiment,
  ScienceExperimentState,
  PartModuleSolarPanel,
  SolarPanelState,
  PartModuleGenerator,
  GeneratorResourceFlow,
  PartModuleLight,
  PartModuleParachute,
  ParachuteState,
  ParachuteSafeState,
  PartModuleCommand,
  CommandControlState,
  PartModuleReactionWheel,
  ReactionWheelState,
  PartModuleRcs,
  PartModuleDecoupler,
  PartModuleDataTransmitter,
  AntennaType,
  PartModuleDeployableAntenna,
  PartModuleDeployableRadiator,
  PartModuleActiveRadiator,
  PartModuleResourceHarvester,
  HarvesterType,
  PartModuleResourceConverter,
  PartModuleControlSurface,
  PartModuleAlternator,
  PawEvent,
} from '../core/part-data';
import type {
  PartCatalogData,
  PartCatalogEntry,
} from '../core/part-catalog-data';
import { CATEGORY_BY_INDEX } from '../core/part-catalog-data';

type ClockWire = [number, number | null];
type GameWire = [string, string | null, number];
type FlightWire = [
  string,                              // vesselId
  number,                              // altitudeAsl
  number,                              // altitudeRadar
  [number, number, number],            // surfaceVelocity
  [number, number, number],            // orbitalVelocity
  number,                              // throttle
  boolean,                             // sas
  boolean,                             // rcs
  [number, number, number, number],    // orientation quat (x, y, z, w)
  [number, number, number],            // angular velocity
  [number, number, number] | null,     // target-relative velocity, null if no target
  number,                              // deltaVMission (m/s)
  number,                              // currentThrust (kN)
  number,                              // stageIdx
  number,                              // deltaVStage (m/s)
  number,                              // twrStage
  0 | 1 | 2,                           // speedDisplayMode byte (orbit/surface/target)
];

const SPEED_DISPLAY_MODE: readonly SpeedDisplayMode[] = ['orbit', 'surface', 'target'];

// Mutable mirrors of the public readonly types. Used only inside
// decoders; consumers see the readonly public type.

interface ClockMutable {
  ut: number;
  met: number | null;
}

interface GameMutable {
  scene: string;
  activeVesselId: string | null;
  timewarp: number;
}

interface FlightMutable {
  vesselId: string;
  altitudeAsl: number;
  altitudeRadar: number;
  surfaceVelocity: Vector3;
  orbitalVelocity: Vector3;
  throttle: number;
  sas: boolean;
  rcs: boolean;
  orientation: Quaternion;
  angularVelocity: Vector3;
  hasTarget: boolean;
  targetVelocity: Vector3;
  deltaVMission: number;
  currentThrust: number;
  stageIdx: number;
  deltaVStage: number;
  twrStage: number;
  speedDisplayMode: SpeedDisplayMode;
}

const clockScratch: ClockMutable = { ut: 0, met: null };

const gameScratch: GameMutable = {
  scene: '',
  activeVesselId: null,
  timewarp: 1,
};

const flightScratch: FlightMutable = {
  vesselId: '',
  altitudeAsl: 0,
  altitudeRadar: 0,
  surfaceVelocity: new Vector3(),
  orbitalVelocity: new Vector3(),
  throttle: 0,
  sas: false,
  rcs: false,
  orientation: new Quaternion(),
  angularVelocity: new Vector3(),
  hasTarget: false,
  targetVelocity: new Vector3(),
  deltaVMission: 0,
  currentThrust: 0,
  stageIdx: -1,
  deltaVStage: 0,
  twrStage: 0,
  speedDisplayMode: 'surface',
};

export function decodeClock(raw: unknown): ClockData {
  const a = raw as ClockWire;
  clockScratch.ut = a[0];
  clockScratch.met = a[1];
  return clockScratch as ClockData;
}

// Config wire payload is the raw config.json contents — the plugin
// never parses it, and the UI deserialises opportunistically against
// its own schema. Decoder is a pass-through: if the top-level value
// is an object, hand it through; anything else (array, primitive,
// null) degrades to `{}` so consumers can rely on object access.
export function decodeConfig(raw: unknown): ConfigData {
  return (raw !== null && typeof raw === 'object' && !Array.isArray(raw))
    ? (raw as ConfigData)
    : {};
}

// EditorState wire is a one-tuple positional array [heldPart].
// Scratch singleton to match the other decoders; callers must copy out.
type EditorStateWire = [string | null];
const editorStateScratch: { heldPart: string | null } = { heldPart: null };
export function decodeEditorState(raw: unknown): EditorStateData {
  const a = raw as EditorStateWire;
  editorStateScratch.heldPart = a[0] ?? null;
  return editorStateScratch as EditorStateData;
}

export function decodeGame(raw: unknown): GameData {
  const a = raw as GameWire;
  gameScratch.scene = a[0];
  gameScratch.activeVesselId = a[1];
  gameScratch.timewarp = a[2];
  return gameScratch as GameData;
}

export function decodeFlight(raw: unknown): FlightData {
  const a = raw as FlightWire;
  flightScratch.vesselId = a[0];
  flightScratch.altitudeAsl = a[1];
  flightScratch.altitudeRadar = a[2];
  const vs = a[3];
  flightScratch.surfaceVelocity.set(vs[0], vs[1], vs[2]);
  const vo = a[4];
  flightScratch.orbitalVelocity.set(vo[0], vo[1], vo[2]);
  flightScratch.throttle = a[5];
  flightScratch.sas = a[6];
  flightScratch.rcs = a[7];
  const q = a[8];
  flightScratch.orientation.set(q[0], q[1], q[2], q[3]);
  const w = a[9];
  flightScratch.angularVelocity.set(w[0], w[1], w[2]);
  const vt = a[10];
  if (vt === null) {
    flightScratch.hasTarget = false;
    flightScratch.targetVelocity.set(0, 0, 0);
  } else {
    flightScratch.hasTarget = true;
    flightScratch.targetVelocity.set(vt[0], vt[1], vt[2]);
  }
  flightScratch.deltaVMission = a[11];
  flightScratch.currentThrust = a[12];
  flightScratch.stageIdx = a[13];
  flightScratch.deltaVStage = a[14];
  flightScratch.twrStage = a[15];
  flightScratch.speedDisplayMode = SPEED_DISPLAY_MODE[a[16]];
  return flightScratch as FlightData;
}

// Engine topic. Wire:
//   [vesselId, [
//     [id, mapX, mapY, status, maxThrust, isp,
//      [crossfeedPartId, ...],
//      [[propName, propAbbr, amount, capacity], ...]
//     ], ...
//   ]]
// Status byte 0=burning, 1=flameout, 2=failed, 3=shutdown, 4=idle —
// mirror of EngineTopic.Classify on the KSP side.
type EnginePropellantWire = [string, string, number, number];
type EngineWire = [
  string,                                    // vesselId
  Array<[
    string,                                  // id
    number,                                  // mapX
    number,                                  // mapY
    0 | 1 | 2 | 3 | 4,                        // status byte
    number,                                  // maxThrust
    number,                                  // isp
    string[],                                // crossfeed part ids
    EnginePropellantWire[],                  // propellants
  ]>,
];

const ENGINE_STATUS: readonly EngineStatus[] = [
  'burning',
  'flameout',
  'failed',
  'shutdown',
  'idle',
];

// Engines array gets replaced wholesale each frame so consumers'
// `$derived` (and any engine-map layout recompute) re-runs on
// material change. Keep the `EngineData` envelope itself as a
// scratch singleton, consistent with the other decoders.
interface EngineMutable {
  vesselId: string;
  engines: readonly EnginePoint[];
}

const engineScratch: EngineMutable = {
  vesselId: '',
  engines: [],
};

export function decodeEngines(raw: unknown): EngineData {
  const a = raw as EngineWire;
  engineScratch.vesselId = a[0];
  const src = a[1];
  const out = new Array<EnginePoint>(src.length);
  for (let i = 0; i < src.length; i++) {
    const e = src[i];
    const propsRaw = e[7];
    const props = new Array(propsRaw.length);
    for (let j = 0; j < propsRaw.length; j++) {
      const p = propsRaw[j];
      props[j] = {
        resourceName: p[0],
        abbr: p[1],
        available: p[2],
        capacity: p[3],
      };
    }
    out[i] = {
      id: e[0],
      x: e[1],
      y: e[2],
      status: ENGINE_STATUS[e[3]],
      maxThrust: e[4],
      isp: e[5],
      crossfeedPartIds: e[6],
      propellants: props,
    };
  }
  engineScratch.engines = out;
  return engineScratch as EngineData;
}

// Stage topic. Wire:
//   [vesselId, currentStageIdx, [
//     [stageNum, dvActual, twrActual,
//      [[kind, persistentId, iconName, cousinsInStage], ...]
//     ], ...
//   ]]
// `kind` is one of: 'engine' | 'decoupler' | 'parachute' | 'clamp' |
// 'other' — the C# side classifies by module scan.
// `cousinsInStage` is the persistentIds of symmetry cousins sharing
// this stage; empty for singletons.
type StagingPartWire = [string, string, string, string[]];
type StageEntryWire = [number, number, number, StagingPartWire[]];
type StageWire = [string, number, StageEntryWire[]];

// Stages array is replaced wholesale each frame so consumers'
// `$derived` computations re-run on material change. Envelope stays a
// scratch singleton for consistency with the other decoders.
interface StageMutable {
  vesselId: string;
  currentStageIdx: number;
  stages: readonly StageEntry[];
}

const stageScratch: StageMutable = {
  vesselId: '',
  currentStageIdx: -1,
  stages: [],
};

export function decodeStage(raw: unknown): StageData {
  const a = raw as StageWire;
  stageScratch.vesselId = a[0];
  stageScratch.currentStageIdx = a[1];
  const src = a[2];
  const out = new Array<StageEntry>(src.length);
  for (let i = 0; i < src.length; i++) {
    const s = src[i];
    const partsRaw = s[3];
    const parts = new Array<StagingPart>(partsRaw.length);
    for (let j = 0; j < partsRaw.length; j++) {
      const p = partsRaw[j];
      parts[j] = {
        kind: p[0] as StagingPartKind,
        persistentId: p[1],
        iconName: p[2],
        cousinsInStage: p[3],
      };
    }
    out[i] = {
      stageNum: s[0],
      deltaVActual: s[1],
      twrActual: s[2],
      parts,
    };
  }
  stageScratch.stages = out;
  return stageScratch as StageData;
}

// PAW topic. Wire:
//   [persistentId]
//     persistentId : decimal-string KSP Part.persistentId, or absent
//                    when the pulse carries no id (defensive — server
//                    currently always provides one).
//
// Event-only: the broadcaster skips snapshot caching for this topic,
// so each dispatch is a fresh pulse. The UI rune treats it as "open
// a PAW for this id" and dedupes against its current open-set.
type PawWire = [string?];

const pawScratch: { persistentId: string } = { persistentId: '' };

export function decodePaw(raw: unknown): PawEvent {
  const a = raw as PawWire;
  pawScratch.persistentId = a[0] ?? '';
  return pawScratch as PawEvent;
}

// PartTopic(id). Wire:
//   [persistentId, name, [screenX, screenY, visible],
//    [[resourceName, abbr, available, capacity], ...],
//    [module, ...]]
//
// Each `module` row is itself tagged by a one-character kind in its
// first element, followed by the common prefix (moduleName, events,
// fields) and then per-kind extras. See `decodeModule` below for
// the full dispatch.
//
// Fields rows are also tagged-union by first element:
//   ['L', id, label, value]                                — label
//   ['T', id, label, value, enabledText, disabledText]     — toggle
//   ['R', id, label, value, min, max, step]                — slider (UI_FloatRange)
//   ['N', id, label, value, min, max, incL, incS, incSl, unit] — numeric (UI_FloatEdit)
//   ['O', id, label, selectedIndex, displays[]]            — dropdown (UI_ChooseOption)
//   ['P', id, label, value, min, max]                      — progress bar
//
// Screen coordinates come off the wire in **Unity physical pixels**
// — WorldToScreenPoint on KSP's camera returns backing-buffer
// coords, and CEF's viewport is resized to match Screen.width /
// Screen.height. On a Retina display those are 2× CSS pixels, so we
// divide by devicePixelRatio here to give the UI consumers a
// coordinate system that matches `window.innerWidth` / CSS layout.
// Mirrors the dg-sidecar's own `dip_x = evt.x / device_scale`
// correction for incoming mouse events (see crates/dg-sidecar/src/
// main.rs :: inject_input_event).
//
// The UI freezes the PAW at the last-known position when `visible`
// is false (part behind camera).
type PartResourceWire = [string, string, number, number];
// Module rows and field rows are tagged unions decoded in
// `decodeModule` / `decodeField` below. Event rows stay flat
// [name, guiName] and are decoded inline.
type PartModuleWire = unknown[];
type PartWire = [
  string,                                          // persistentId
  string,                                          // name
  [number, number, boolean],                       // screen: [x, y, visible]
  PartResourceWire[],                              // resources
  PartModuleWire[]?,                               // modules (optional for forward-compat)
];

interface PartMutable {
  persistentId: string;
  name: string;
  screen: { x: number; y: number; visible: boolean } | null;
  resources: readonly PartResourceData[];
  modules: readonly PartModuleData[];
}

// Fresh per-decode rather than a module-scoped scratch: multiple open
// PAWs share this decoder, and a per-decoder scratch would let the
// last-decoded frame's fields leak into an earlier PAW's store on
// reentrant dispatch. The allocation is cheap at 10 Hz × N open
// PAWs (N is ≤ ~8 in realistic use).
export function decodePart(raw: unknown): PartData {
  const a = raw as PartWire;
  const screenRaw = a[2];
  const dpr = typeof window !== 'undefined' && window.devicePixelRatio > 0
    ? window.devicePixelRatio
    : 1;
  const screen = screenRaw !== undefined && screenRaw !== null
    ? { x: screenRaw[0] / dpr, y: screenRaw[1] / dpr, visible: screenRaw[2] }
    : null;
  const resourcesRaw = a[3] ?? [];
  const resources = new Array<PartResourceData>(resourcesRaw.length);
  for (let i = 0; i < resourcesRaw.length; i++) {
    const r = resourcesRaw[i];
    resources[i] = {
      name: r[0],
      abbr: r[1],
      available: r[2],
      capacity: r[3],
    };
  }
  const modulesRaw = a[4] ?? [];
  const modules = new Array<PartModuleData>(modulesRaw.length);
  for (let i = 0; i < modulesRaw.length; i++) {
    modules[i] = decodeModule(modulesRaw[i]);
  }
  const frame: PartMutable = {
    persistentId: a[0],
    name: a[1],
    screen,
    resources,
    modules,
  };
  return frame as PartData;
}

// Field row decoder — discriminator is the first element (one
// character for wire compactness). Unknown kinds degrade to a label
// carrying their string repr so a mismatched client / server version
// still shows *something* rather than swallowing the row.
function decodeField(raw: unknown): PartFieldData {
  const a = raw as unknown[];
  const kind = a[0] as string;
  switch (kind) {
    case 'L':
      return {
        kind: 'label',
        id: a[1] as string,
        label: a[2] as string,
        value: a[3] as string,
      };
    case 'T':
      return {
        kind: 'toggle',
        id: a[1] as string,
        label: a[2] as string,
        value: a[3] as boolean,
        enabledText: a[4] as string,
        disabledText: a[5] as string,
      };
    case 'R':
      return {
        kind: 'slider',
        id: a[1] as string,
        label: a[2] as string,
        value: a[3] as number,
        min: a[4] as number,
        max: a[5] as number,
        step: a[6] as number,
      };
    case 'N':
      return {
        kind: 'numeric',
        id: a[1] as string,
        label: a[2] as string,
        value: a[3] as number,
        min: a[4] as number,
        max: a[5] as number,
        incLarge: a[6] as number,
        incSmall: a[7] as number,
        incSlide: a[8] as number,
        unit: a[9] as string,
      };
    case 'O':
      return {
        kind: 'option',
        id: a[1] as string,
        label: a[2] as string,
        selectedIndex: a[3] as number,
        display: a[4] as readonly string[],
      };
    case 'P':
      return {
        kind: 'progress',
        id: a[1] as string,
        label: a[2] as string,
        value: a[3] as number,
        min: a[4] as number,
        max: a[5] as number,
      };
    default:
      return {
        kind: 'label',
        id: String(a[1] ?? ''),
        label: String(a[2] ?? kind),
        value: `(unsupported: ${kind})`,
      };
  }
}

// Module row decoder. Wire shape per kind:
//   ['G', moduleName, events, fields]                      — generic
//   ['E', moduleName, status, thrustLimit, currentThrust,
//        maxThrust, realIsp, [propellants]]                — ModuleEngines
//   ['S', moduleName, sensorType, active, value, unit,
//        statusText]                                       — ModuleEnviroSensor
//
// Typed kinds do NOT carry the generic events/fields arrays — the
// bespoke renderer knows the schema and drives invokeEvent /
// setField by hard-coded KSP member names.
function decodeModule(raw: unknown): PartModuleData {
  const a = raw as unknown[];
  const kind = a[0] as string;
  const moduleName = a[1] as string;

  switch (kind) {
    case 'E': {
      const propellantsRaw = (a[7] as unknown[] | undefined) ?? [];
      const propellants = new Array<PartEnginePropellant>(propellantsRaw.length);
      for (let i = 0; i < propellantsRaw.length; i++) {
        const p = propellantsRaw[i] as unknown[];
        propellants[i] = {
          name: p[0] as string,
          displayName: p[1] as string,
          ratio: p[2] as number,
          currentAmount: p[3] as number,
          totalAvailable: p[4] as number,
        };
      }
      const out: PartModuleEngines = {
        kind: 'engines',
        moduleName,
        status: (a[2] as PartEngineStatus) ?? 'shutdown',
        thrustLimit: (a[3] as number) ?? 100,
        currentThrust: (a[4] as number) ?? 0,
        maxThrust: (a[5] as number) ?? 0,
        realIsp: (a[6] as number) ?? 0,
        propellants,
      };
      return out;
    }
    case 'S': {
      const out: PartModuleEnviroSensor = {
        kind: 'sensor',
        moduleName,
        sensorType: (a[2] as EnviroSensorType) ?? 'temperature',
        active: (a[3] as boolean) ?? false,
        value: (a[4] as number) ?? 0,
        unit: (a[5] as string) ?? '',
        statusText: (a[6] as string) ?? 'Off',
      };
      return out;
    }
    case 'X': {
      const out: PartModuleScienceExperiment = {
        kind: 'science',
        moduleName,
        experimentTitle: (a[2] as string) ?? '',
        state: (a[3] as ScienceExperimentState) ?? 'stowed',
        rerunnable: (a[4] as boolean) ?? false,
        transmitValue: (a[5] as number) ?? 0,
        dataAmount: (a[6] as number) ?? 0,
      };
      return out;
    }
    case 'V': {
      const out: PartModuleSolarPanel = {
        kind: 'solar',
        moduleName,
        state: (a[2] as SolarPanelState) ?? 'retracted',
        flowRate: (a[3] as number) ?? 0,
        chargeRate: (a[4] as number) ?? 0,
        sunAOA: (a[5] as number) ?? 0,
        retractable: (a[6] as boolean) ?? true,
        isTracking: (a[7] as boolean) ?? false,
      };
      return out;
    }
    case 'R': {
      const inRaw = (a[6] as unknown[] | undefined) ?? [];
      const outRaw = (a[7] as unknown[] | undefined) ?? [];
      const pickFlow = (row: unknown): GeneratorResourceFlow => {
        const r = row as unknown[];
        return { name: r[0] as string, rate: r[1] as number };
      };
      const out: PartModuleGenerator = {
        kind: 'generator',
        moduleName,
        active: (a[2] as boolean) ?? false,
        alwaysOn: (a[3] as boolean) ?? false,
        efficiency: (a[4] as number) ?? 0,
        status: (a[5] as string) ?? '',
        inputs: inRaw.map(pickFlow),
        outputs: outRaw.map(pickFlow),
      };
      return out;
    }
    case 'L': {
      const out: PartModuleLight = {
        kind: 'light',
        moduleName,
        on: (a[2] as boolean) ?? false,
        r: (a[3] as number) ?? 1,
        g: (a[4] as number) ?? 1,
        b: (a[5] as number) ?? 1,
      };
      return out;
    }
    case 'C': {
      const out: PartModuleParachute = {
        kind: 'parachute',
        moduleName,
        state: (a[2] as ParachuteState) ?? 'stowed',
        safeState: (a[3] as ParachuteSafeState) ?? 'none',
        deployAltitude: (a[4] as number) ?? 1000,
        minPressure: (a[5] as number) ?? 0.01,
      };
      return out;
    }
    case 'M': {
      const out: PartModuleCommand = {
        kind: 'command',
        moduleName,
        crewCount: (a[2] as number) ?? 0,
        minimumCrew: (a[3] as number) ?? 0,
        controlState: (a[4] as CommandControlState) ?? 'nominal',
        hibernate: (a[5] as boolean) ?? false,
        hibernateOnWarp: (a[6] as boolean) ?? false,
      };
      return out;
    }
    case 'W': {
      const out: PartModuleReactionWheel = {
        kind: 'reactionWheel',
        moduleName,
        state: (a[2] as ReactionWheelState) ?? 'disabled',
        authorityLimiter: (a[3] as number) ?? 100,
        pitchTorque: (a[4] as number) ?? 0,
        yawTorque: (a[5] as number) ?? 0,
        rollTorque: (a[6] as number) ?? 0,
        actuatorMode: (a[7] as number) ?? 0,
      };
      return out;
    }
    case 'T': {
      const propRaw = (a[6] as unknown[] | undefined) ?? [];
      const propellants = new Array<PartEnginePropellant>(propRaw.length);
      for (let i = 0; i < propRaw.length; i++) {
        const p = propRaw[i] as unknown[];
        propellants[i] = {
          name: p[0] as string,
          displayName: p[1] as string,
          ratio: p[2] as number,
          currentAmount: p[3] as number,
          totalAvailable: p[4] as number,
        };
      }
      const out: PartModuleRcs = {
        kind: 'rcs',
        moduleName,
        enabled: (a[2] as boolean) ?? true,
        thrustLimit: (a[3] as number) ?? 100,
        thrusterPower: (a[4] as number) ?? 0,
        realIsp: (a[5] as number) ?? 0,
        propellants,
      };
      return out;
    }
    case 'D': {
      const out: PartModuleDecoupler = {
        kind: 'decoupler',
        moduleName,
        isDecoupled: (a[2] as boolean) ?? false,
        isAnchored: (a[3] as boolean) ?? false,
        ejectionForce: (a[4] as number) ?? 0,
      };
      return out;
    }
    case 'A': {
      const out: PartModuleDataTransmitter = {
        kind: 'transmitter',
        moduleName,
        antennaType: (a[2] as AntennaType) ?? 'direct',
        antennaPower: (a[3] as number) ?? 0,
        packetSize: (a[4] as number) ?? 0,
        packetInterval: (a[5] as number) ?? 0,
        busy: (a[6] as boolean) ?? false,
      };
      return out;
    }
    case 'Y': {
      const out: PartModuleDeployableAntenna = {
        kind: 'deployAntenna',
        moduleName,
        state: (a[2] as SolarPanelState) ?? 'retracted',
        retractable: (a[3] as boolean) ?? true,
      };
      return out;
    }
    case 'Z': {
      const out: PartModuleDeployableRadiator = {
        kind: 'deployRadiator',
        moduleName,
        state: (a[2] as SolarPanelState) ?? 'retracted',
        retractable: (a[3] as boolean) ?? true,
      };
      return out;
    }
    case 'K': {
      const out: PartModuleActiveRadiator = {
        kind: 'activeRadiator',
        moduleName,
        isCooling: (a[2] as boolean) ?? false,
        maxTransfer: (a[3] as number) ?? 0,
        status: (a[4] as string) ?? '',
      };
      return out;
    }
    case 'J': {
      const out: PartModuleResourceHarvester = {
        kind: 'harvester',
        moduleName,
        active: (a[2] as boolean) ?? false,
        status: (a[3] as string) ?? '',
        resourceName: (a[4] as string) ?? '',
        harvesterType: (a[5] as HarvesterType) ?? 'planetary',
        abundance: (a[6] as number) ?? 0,
        thermalEfficiency: (a[7] as number) ?? 0,
        loadCapacity: (a[8] as number) ?? 0,
      };
      return out;
    }
    case 'U': {
      const inRaw = (a[5] as unknown[] | undefined) ?? [];
      const outRaw = (a[6] as unknown[] | undefined) ?? [];
      const pickFlow = (row: unknown): GeneratorResourceFlow => {
        const r = row as unknown[];
        return { name: r[0] as string, rate: r[1] as number };
      };
      const out: PartModuleResourceConverter = {
        kind: 'converter',
        moduleName,
        active: (a[2] as boolean) ?? false,
        converterName: (a[3] as string) ?? '',
        status: (a[4] as string) ?? '',
        inputs: inRaw.map(pickFlow),
        outputs: outRaw.map(pickFlow),
      };
      return out;
    }
    case 'F': {
      const out: PartModuleControlSurface = {
        kind: 'controlSurface',
        moduleName,
        ignorePitch: (a[2] as boolean) ?? false,
        ignoreYaw: (a[3] as boolean) ?? false,
        ignoreRoll: (a[4] as boolean) ?? false,
        authorityLimiter: (a[5] as number) ?? 100,
        deploy: (a[6] as boolean) ?? false,
        deployInvert: (a[7] as boolean) ?? false,
        deployAngle: (a[8] as number) ?? 0,
      };
      return out;
    }
    case 'N': {
      const out: PartModuleAlternator = {
        kind: 'alternator',
        moduleName,
        outputRate: (a[2] as number) ?? 0,
        outputName: (a[3] as string) ?? '',
        outputUnits: (a[4] as string) ?? '',
        engineRunning: (a[5] as boolean) ?? false,
      };
      return out;
    }
    case 'G':
    default: {
      const events = decodeEvents(a[2] as unknown[] | undefined);
      const fields = decodeFields(a[3] as unknown[] | undefined);
      const out: PartModuleGeneric = {
        kind: 'generic',
        moduleName,
        events,
        fields,
      };
      return out;
    }
  }
}

function decodeEvents(raw: unknown[] | undefined): PartEventData[] {
  const src = raw ?? [];
  const out = new Array<PartEventData>(src.length);
  for (let i = 0; i < src.length; i++) {
    const e = src[i] as unknown[];
    out[i] = { id: e[0] as string, label: e[1] as string };
  }
  return out;
}

function decodeFields(raw: unknown[] | undefined): PartFieldData[] {
  const src = raw ?? [];
  const out = new Array<PartFieldData>(src.length);
  for (let i = 0; i < src.length; i++) out[i] = decodeField(src[i]);
  return out;
}

// PartCatalog: one-shot emission, so no scratch singleton —
// consumers keep the returned array for the duration of the editor
// session. Wire shape per entry:
//   [name, title, categoryIdx, manufacturer, cost, mass,
//    description, techRequired, tags]
export function decodePartCatalog(raw: unknown): PartCatalogData {
  const src = (raw as unknown[]) ?? [];
  const entries = new Array<PartCatalogEntry>(src.length);
  for (let i = 0; i < src.length; i++) {
    const a = src[i] as unknown[];
    const idx = a[2] as number;
    const category = CATEGORY_BY_INDEX[idx] ?? 'Utility';
    entries[i] = {
      name: (a[0] as string) ?? '',
      title: (a[1] as string) ?? '',
      category,
      manufacturer: (a[3] as string) ?? '',
      cost: (a[4] as number) ?? 0,
      mass: (a[5] as number) ?? 0,
      description: (a[6] as string) ?? '',
      techRequired: (a[7] as string) ?? '',
      tags: (a[8] as string) ?? '',
      iconBase64: (a[9] as string) ?? '',
      bulkheadProfiles: (a[10] as string) ?? '',
    };
  }
  return { entries };
}
