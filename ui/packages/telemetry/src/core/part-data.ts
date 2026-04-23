// Per-part telemetry for Part Action Windows (PAWs).
//
// `PartTopic(id)` is a parametrized topic: the name carries the KSP
// `persistentId` of the target part (`part/<id>`), so multiple PAWs
// coexist as independent subscriptions. The server emits a frame
// whenever the part's screen position moves or a resource tick crosses
// a dispatch threshold.
//
// `PawTopic` is an event-only topic — it carries the persistentId of
// whichever part the pilot right-clicked, with no stable value. The UI
// treats every dispatch as "open a PAW for this id".

export interface PartResourceData {
  /** KSP internal resource name, e.g. "LiquidFuel". */
  readonly name: string;
  /** Short label for the HUD, e.g. "LF" / "OX" / "EC". */
  readonly abbr: string;
  /** Units currently stored. */
  readonly available: number;
  /** Units of maximum capacity. */
  readonly capacity: number;
  /**
   * Optional rate of change, units/sec. Positive = inflow,
   * negative = drain. Omit for static resources; the UI will hide
   * the flow indicator.
   */
  readonly flow?: number;
}

export interface PartScreenPos {
  /** CSS pixels from the viewport left edge. */
  readonly x: number;
  /** CSS pixels from the viewport top edge. */
  readonly y: number;
  /** False when the part is behind the camera or off-screen. */
  readonly visible: boolean;
}

/**
 * A click-target on a PartModule — wraps a KSP `BaseEvent`. Typically
 * rendered as a button ("Deploy", "Run Experiment", "Toggle Lights").
 * Only currently-valid events (guiActive && active on the server) are
 * emitted, so the UI doesn't need to filter.
 */
export interface PartEventData {
  /** Stable event identifier — KSP's `BaseEvent.name`. Sent back on
   *  invokeEvent ops so the server can route without trusting the
   *  guiName string. */
  readonly id: string;
  /** Localized display label, e.g. "Toggle Landing Gear". */
  readonly label: string;
}

/**
 * A KSPField row on a PartModule. Each stock `UI_Control` variant
 * maps to one of these shapes — the server disambiguates against
 * `field.uiControlFlight` and emits the matching kind. Unknown /
 * unsupported controls (UI_Vector2, UI_ColorPicker, …) currently
 * fall back to `"label"` so the value is at least visible.
 *
 * Kind letters are single-char for wire compactness — expanded by
 * the decoder into these named objects.
 */
export type PartFieldData =
  /** Read-only formatted text. Stock's `UI_Label` or any guiActive
   *  field whose control isn't otherwise handled. */
  | {
      readonly kind: 'label';
      readonly id: string;
      readonly label: string;
      readonly value: string;
    }
  /** Boolean toggle (`UI_Toggle`). `enabledText` / `disabledText`
   *  are stock's "when pressed, it says X; when not, Y" labels. */
  | {
      readonly kind: 'toggle';
      readonly id: string;
      readonly label: string;
      readonly value: boolean;
      readonly enabledText: string;
      readonly disabledText: string;
    }
  /** Float range slider (`UI_FloatRange`). */
  | {
      readonly kind: 'slider';
      readonly id: string;
      readonly label: string;
      readonly value: number;
      readonly min: number;
      readonly max: number;
      readonly step: number;
    }
  /** Select-one-of-many (`UI_ChooseOption`). `options` holds the
   *  stored values (server writes `options[selectedIndex]` back to
   *  the field); `display` holds the localized human labels. */
  | {
      readonly kind: 'option';
      readonly id: string;
      readonly label: string;
      /** Current selected index; -1 if the field's value isn't
       *  in the options array. */
      readonly selectedIndex: number;
      readonly display: readonly string[];
    }
  /** Numeric edit with large/small/slide increments (`UI_FloatEdit`).
   *  Rendered like a slider but with keyboard-editable value and
   *  coarse / fine buttons. `unit` is a suffix string. */
  | {
      readonly kind: 'numeric';
      readonly id: string;
      readonly label: string;
      readonly value: number;
      readonly min: number;
      readonly max: number;
      readonly incLarge: number;
      readonly incSmall: number;
      readonly incSlide: number;
      readonly unit: string;
    }
  /** Read-only progress bar (`UI_ProgressBar`). Engine ignitions,
   *  experiment progress, robotic travel, etc. */
  | {
      readonly kind: 'progress';
      readonly id: string;
      readonly label: string;
      readonly value: number;
      readonly min: number;
      readonly max: number;
    };

/**
 * Common base for every module row on the wire — just the raw KSP
 * class name, informational. The typed `kind` drives renderer
 * selection; typed modules carry their own fully-custom payload and
 * do NOT ship the generic events/fields arrays. That keeps the wire
 * free of "here's a KSPField called thrustPercentage with a
 * UI_FloatRange" prose when the client already knows the exact
 * structure of a ModuleEngines block.
 */
export interface PartModuleBase {
  /** Raw KSP class name — informational; used for header display
   *  and debugging. `kind` drives renderer selection. */
  readonly moduleName: string;
}

/**
 * Fallback module shape — every PartModule the server doesn't
 * specialise lands here. Carries the generic events + fields arrays
 * so the default renderer can dispatch field widgets and event
 * buttons without knowing the specific module. Renderer:
 * `DefaultModule`.
 */
export interface PartModuleGeneric extends PartModuleBase {
  readonly kind: 'generic';
  /** Events currently available (guiActive && active). */
  readonly events: readonly PartEventData[];
  /** KSPFields with a flight-visible UI_Control. */
  readonly fields: readonly PartFieldData[];
}

/**
 * One propellant entry on a ModuleEngines. Mirrors KSP's own
 * `Propellant` class: a named resource consumed at `ratio` parts
 * per unit thrust, with live accounting of what's reachable through
 * the vessel's crossfeed graph.
 */
export interface PartEnginePropellant {
  /** KSP internal resource name ("LiquidFuel", "Oxidizer", ...). */
  readonly name: string;
  /** Localized display name ("Liquid Fuel"). */
  readonly displayName: string;
  /** Consumption ratio — relative to the other propellants in the
   *  same engine. Stock's "1.1 : 1.0" LF:Ox is displayed from these. */
  readonly ratio: number;
  /** Amount drawn this frame. */
  readonly currentAmount: number;
  /** Total units of this propellant reachable via crossfeed. */
  readonly totalAvailable: number;
}

/** Engine run state, derived from ignited / flameout / finalThrust. */
export type PartEngineStatus = 'burning' | 'idle' | 'flameout' | 'shutdown';

/**
 * Typed shape for `ModuleEngines` (and `ModuleEnginesFX`, which
 * inherits from it). No generic events/fields arrays — the bespoke
 * renderer knows the module's schema and invokes fixed KSP member
 * names via `PartOps`:
 *   - `invokeEvent(moduleIndex, 'Activate')`   / `'Shutdown'`
 *   - `setField(moduleIndex, 'thrustPercentage', 0..100)`
 * The server re-verifies `guiActive`/`active` on receive, so a
 * stale click (e.g. "Shutdown" while already shut down) drops
 * silently.
 */
export interface PartModuleEngines extends PartModuleBase {
  readonly kind: 'engines';
  readonly status: PartEngineStatus;
  /** `ModuleEngines.thrustPercentage` — thrust limiter, 0..100. */
  readonly thrustLimit: number;
  /** Instantaneous thrust in kN (`finalThrust`). */
  readonly currentThrust: number;
  /** Configured max thrust in kN at vacuum (`maxThrust`). */
  readonly maxThrust: number;
  /** Current atmosphere-adjusted Isp, seconds (`realIsp`). */
  readonly realIsp: number;
  readonly propellants: readonly PartEnginePropellant[];
}

/** Environmental sensor reading type (`ModuleEnviroSensor.sensorType`). */
export type EnviroSensorType =
  | 'temperature'
  | 'pressure'
  | 'gravity'
  | 'acceleration';

/**
 * Typed shape for `ModuleEnviroSensor`, the module backing the stock
 * 2HOT Thermometer, PresMat Barometer, GRAVMAX Gravioli Detector,
 * and Double-C Seismic Accelerometer.
 *
 * Stock's `readoutInfo` KSPField only refreshes while the stock PAW
 * is showing the part — we suppress that, so the server computes
 * the reading itself from `part.temperature` / `vessel.staticPressurekPa`
 * / `vessel.geeForce` / `FlightGlobals.getGeeForceAtPosition`, then
 * hands the typed payload to the UI.
 *
 * The renderer toggles the sensor via:
 *   `invokeEvent(moduleIndex, 'Toggle')`
 */
export interface PartModuleEnviroSensor extends PartModuleBase {
  readonly kind: 'sensor';
  readonly sensorType: EnviroSensorType;
  /** `ModuleEnviroSensor.sensorActive` — on/off. */
  readonly active: boolean;
  /** Current reading in `unit`'s units. Meaningless when
   *  `statusText` is non-"Active" (e.g. "Off", "No Atm"). */
  readonly value: number;
  /** Unit suffix for the reading (`"K"`, `"kPa"`, `"m/s²"`, `"g"`),
   *  or empty string when no valid reading is available. */
  readonly unit: string;
  /** "Active" when the reading is valid; otherwise a short reason
   *  ("Off" / "Out of Range" / "Trace Atm" / "No Atm"). Mirrors stock's
   *  edge-case vocabulary in ModuleEnviroSensor.readoutInfo. */
  readonly statusText: string;
}

/**
 * Typed shape for `ModuleScienceExperiment` — the module behind every
 * stock science instrument (thermometer, goo, science jr., etc.).
 * State-machine: stowed → ready (data collected) → inoperable (used
 * up on a non-rerunnable part).
 *
 * Renderer actions via `invokeEvent`:
 *   - `'DeployExperiment'` when `state === 'stowed'`
 *   - `'ReviewDataEvent'` / `'ResetExperiment'` when `state === 'ready'`
 */
export type ScienceExperimentState = 'stowed' | 'ready' | 'inoperable';

export interface PartModuleScienceExperiment extends PartModuleBase {
  readonly kind: 'science';
  /** Localized experiment title ("Temperature Scan", "Mystery Goo
   *  Observation", ...). Falls back to experimentID on older parts. */
  readonly experimentTitle: string;
  readonly state: ScienceExperimentState;
  readonly rerunnable: boolean;
  /** Science points the pilot would bank on Transmit right now —
   *  `baseTransmitValue × transmitBonus`. Zero when no data stored. */
  readonly transmitValue: number;
  /** Raw data amount in Mits. Zero when no data stored. */
  readonly dataAmount: number;
}

/**
 * Typed shape for `ModuleDeployableSolarPanel`. Five-state deploy
 * ladder, live flow rate + max capacity, and the sun angle of attack
 * the panel's currently getting.
 *
 * Renderer actions via `invokeEvent`: `'Extend'` / `'Retract'` (when
 * `retractable`).
 */
export type SolarPanelState =
  | 'retracted'
  | 'extending'
  | 'extended'
  | 'retracting'
  | 'broken';

export interface PartModuleSolarPanel extends PartModuleBase {
  readonly kind: 'solar';
  readonly state: SolarPanelState;
  /** Current EC/s output (0 when retracted, broken, or occluded). */
  readonly flowRate: number;
  /** Max configured EC/s — the capped output when AOA=1 at 1 AU. */
  readonly chargeRate: number;
  /** Sun angle of attack, 0..1 (cos of the panel-normal/sun angle). */
  readonly sunAOA: number;
  readonly retractable: boolean;
  readonly isTracking: boolean;
}

/**
 * Typed shape for `ModuleGenerator` — RTGs, fuel cells, and any other
 * always-on or toggleable resource producer/consumer. Both inputs and
 * outputs are emitted so the UI shows fuel-cell consumption as well
 * as production.
 *
 * Renderer actions via `invokeEvent`: `'Activate'` / `'Shutdown'`
 * (ignored by RTGs / any `alwaysOn` generator).
 */
export interface PartModuleGenerator extends PartModuleBase {
  readonly kind: 'generator';
  readonly active: boolean;
  readonly alwaysOn: boolean;
  readonly efficiency: number;
  /** Stock's short status string ("Nominal", "Lacking: LiquidFuel"). */
  readonly status: string;
  readonly inputs: readonly GeneratorResourceFlow[];
  readonly outputs: readonly GeneratorResourceFlow[];
}

export interface GeneratorResourceFlow {
  readonly name: string;
  /** Units/sec at 100% efficiency. Multiply by `efficiency` for the
   *  actual instantaneous rate. */
  readonly rate: number;
}

/**
 * Typed shape for `ModuleLight` — part-mounted lights. RGB channels
 * are Unity Color floats 0..1.
 *
 * Renderer actions via `invokeEvent`: `'LightsOn'` / `'LightsOff'`
 * (or the combined `'ToggleLights'` when we want one button).
 */
export interface PartModuleLight extends PartModuleBase {
  readonly kind: 'light';
  readonly on: boolean;
  readonly r: number;
  readonly g: number;
  readonly b: number;
}

/**
 * Typed shape for `ModuleParachute`. Five-state deployment ladder
 * plus the stock "safe / risky / unsafe" deployability indicator
 * that tells the pilot whether firing right now would shred the
 * canopy.
 *
 * Renderer actions via `invokeEvent`: `'Deploy'` / `'CutParachute'` /
 * `'Repack'` (depending on state).
 */
export type ParachuteState = 'stowed' | 'active' | 'semi' | 'deployed' | 'cut';
export type ParachuteSafeState = 'safe' | 'risky' | 'unsafe' | 'none';

export interface PartModuleParachute extends PartModuleBase {
  readonly kind: 'parachute';
  readonly state: ParachuteState;
  readonly safeState: ParachuteSafeState;
  /** Altitude (metres) at which a staged chute fully deploys. */
  readonly deployAltitude: number;
  /** Min atmospheric pressure (atm) required to deploy. */
  readonly minPressure: number;
}

export type PartModuleData =
  | PartModuleGeneric
  | PartModuleEngines
  | PartModuleEnviroSensor
  | PartModuleScienceExperiment
  | PartModuleSolarPanel
  | PartModuleGenerator
  | PartModuleLight
  | PartModuleParachute;

export interface PartData {
  readonly persistentId: string;
  /** Localized part title (e.g. "RT-10 'Hammer' Solid Fuel Booster"). */
  readonly name: string;
  /** Viewport-space projection of the part centre; null until first frame. */
  readonly screen: PartScreenPos | null;
  readonly resources: readonly PartResourceData[];
  /** PartModules on this part, in the same order KSP lists them
   *  (`Part.Modules`). The order is stable for a given part, so it's
   *  safe to use the index to address a module in op calls. */
  readonly modules: readonly PartModuleData[];
}

/**
 * Ops the UI sends back through `PartTopic(id)`.
 */
export interface PartOps {
  /**
   * Click a button on a PartModule. The server resolves the target
   * module by index within `part.Modules` and the event by `name` on
   * that module, then invokes it on the main thread.
   */
  invokeEvent(moduleIndex: number, eventId: string): void;
  /**
   * Write a new value to a KSPField on a PartModule. The payload
   * type depends on the field's kind — the server casts as needed:
   *   - toggle       → boolean
   *   - slider/numeric → number
   *   - option       → index (number) into the option display list
   * Unknown / out-of-range writes are silently dropped.
   */
  setField(moduleIndex: number, fieldId: string, value: boolean | number): void;
}

/**
 * PAW open event. No stable value — each dispatch is a pulse telling
 * the UI to open a window for `persistentId`. Handlers dedupe against
 * their current open-set; re-right-clicking an open part is a no-op.
 *
 * No ops: the "which parts are we watching" question is answered by
 * subscribing to `PartTopic(id)`. The transport layer translates
 * first-subscriber / last-unsubscriber transitions into reserved
 * `subscribe` / `unsubscribe` signals on the wire, so the server
 * spins up or tears down the matching per-part feed without any
 * app-level handshake here.
 */
export interface PawEvent {
  readonly persistentId: string;
}
