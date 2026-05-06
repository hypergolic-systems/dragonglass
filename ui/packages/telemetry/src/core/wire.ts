// Wire-format types for built-in Dragonglass topics.
//
// The transport delivers `data` payloads to subscribers as raw JSON
// — the topic's wire shape — and leaves all decoding to the
// subscriber. Hooks in `../svelte/` use the decoders in
// `../dragonglass/decoders.ts` to turn these tuples into the named
// `*Data` objects they expose to UI components.
//
// Each entry below mirrors the C# topic's `WriteData` exactly.
// Editing one requires editing the matching server-side topic in
// `mod/Dragonglass.Telemetry/src/Topics/`.

// Clock topic. Wire: [universalTime, missionElapsedTime|null].
export type ClockWire = [number, number | null];

// Game topic. Wire: [scene, activeVesselId|null, timewarp, mapActive].
export type GameWire = [string, string | null, number, boolean];

// Flight topic. Wire: vessel + nav-instrument frame, positional.
export type FlightWire = [
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
  [number, number, number] | null,     // target-relative velocity
  number,                              // deltaVMission (m/s)
  number,                              // currentThrust (kN)
  number,                              // stageIdx
  number,                              // deltaVStage (m/s)
  number,                              // twrStage
  0 | 1 | 2,                           // speedDisplayMode (orbit/surface/target)
];

// EditorState topic. Wire: [heldPartName | null].
export type EditorStateWire = [string | null];

// Engine topic. Wire: [vesselId, [enginePoint, ...]].
export type EnginePropellantWire = [
  string,  // resourceName
  string,  // abbr
  number,  // available
  number,  // capacity
];

export type EnginePointWire = [
  string,                                // id
  number,                                // mapX
  number,                                // mapY
  0 | 1 | 2 | 3 | 4,                     // status byte
  number,                                // throttle (0..1)
  number,                                // maxThrust
  number,                                // isp
  string[],                              // crossfeed part ids
  EnginePropellantWire[],                // propellants
];

export type EngineWire = [string, EnginePointWire[]];

// Stage topic. Wire: [vesselId, currentStageIdx, [stageEntry, ...]].
export type StagingPartWire = [
  string,    // kind
  string,    // persistentId
  string,    // iconName
  string[],  // cousinsInStage
];

export type StageEntryWire = [
  number,                // stageNum
  number,                // dvActual
  number,                // twrActual
  StagingPartWire[],
];

export type StageWire = [string, number, StageEntryWire[]];

// PAW open-intent pulse. Wire: [persistentId?].
export type PawWire = [string?];

// Part topic. Wire: per-part state + module rows.
export type PartResourceWire = [
  string,  // resourceName
  string,  // abbr
  number,  // available
  number,  // capacity
];

// Module rows are tagged-union positional arrays — the kind char
// in slot 0 selects the rest of the schema. Decoded by
// `decodeModule` in the transport-side decoders.
export type PartModuleWire = unknown[];

export type PartWire = [
  string,                              // persistentId
  string,                              // name
  [number, number, boolean],           // screen: [x, y, visible]
  PartResourceWire[],                  // resources
  PartModuleWire[],                    // modules
  number,                              // distanceFromActiveM
];

// Parts catalog. One-shot emission per editor entry. Wire is an
// array of positional rows, one per part-prefab.
export type PartCatalogEntryWire = [
  string,  // name
  string,  // title
  number,  // categoryIdx
  string,  // manufacturer
  number,  // cost
  number,  // mass
  string,  // description
  string,  // techRequired
  string,  // tags
  string,  // iconBase64
  string,  // bulkheadProfiles
];

export type PartCatalogWire = PartCatalogEntryWire[];

// Portraits topic. Wire: [[entry, ...]] where each entry describes
// one Kerbal portrait the native plugin currently has a chroma-key
// stream registered for. The UI mounts a `<PunchThrough id={id} />`
// per entry, optionally annotating with name/role/level.
export type PortraitEntryWire = [
  string,  // id ("kerbal:Jeb")
  string,  // name ("Jebediah Kerman")
  string,  // role ("Pilot")
  number,  // level (0–5)
];
export type PortraitsWire = [PortraitEntryWire[]];
