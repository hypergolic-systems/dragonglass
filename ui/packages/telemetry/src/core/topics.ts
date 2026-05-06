// Topic factories. The generic `T` is the **wire shape** the
// transport delivers to subscribers — all decoding into the named
// `*Data` types lives in subscriber code (the hooks under
// `../svelte/`). The transport itself doesn't decode.
//
// `ConfigData` and `AssemblyModel` have no positional wire form —
// they ship as plain JSON objects, so the wire and consumer types
// are the same.

import { topic, type Topic } from './ksp';
import type { ConfigData } from './config-data';
import type { GameOps } from './game-ops';
import type { FlightOps } from './flight-ops';
import type { AssemblyModel } from './assembly';
import type { StageOps } from './stage-ops';
import type { PartOps } from './part-data';
import type { PartCatalogOps } from './part-catalog-data';
import type {
  ClockWire,
  GameWire,
  EditorStateWire,
  FlightWire,
  EngineWire,
  StageWire,
  PawWire,
  PartWire,
  PartCatalogWire,
  PortraitsWire,
} from './wire';

export const ClockTopic = topic<ClockWire>('clock');
export const ConfigTopic = topic<ConfigData>('config');
export const EditorStateTopic = topic<EditorStateWire>('editorState');
export const GameTopic = topic<GameWire, GameOps>('game');
export const FlightTopic = topic<FlightWire, FlightOps>('flight');
export const AssemblyTopic = topic<AssemblyModel>('assembly');
export const EngineTopic = topic<EngineWire>('engines');
export const StageTopic = topic<StageWire, StageOps>('stage');

/**
 * PAW open-intent pulses (persistentId of the part the pilot
 * right-clicked). Treat each dispatch as an event, not a state — see
 * `part-data.ts`.
 */
export const PawTopic = topic<PawWire>('paw');

/**
 * VAB/SPH parts catalog. One-shot emission on editor entry; the
 * server doesn't re-emit mid-session because loaded parts don't
 * change at runtime. Consumers should cache the first frame and
 * reuse it for the duration of the editor scene.
 */
export const PartCatalogTopic = topic<PartCatalogWire, PartCatalogOps>('partCatalog');

/**
 * Active Kerbal portrait roster — one entry per portrait the native
 * plugin currently has a chroma-key stream registered for. The HUD
 * mounts a `<PunchThrough id={entry.id} />` per active member; the
 * compositor reveals the live IVA face beneath the placeholder.
 */
export const PortraitsTopic = topic<PortraitsWire>('portraits');

/**
 * Per-part telemetry topic. The topic name embeds the part's KSP
 * persistentId so multiple open PAWs subscribe independently; the
 * server only maintains the frame stream for parts with at least one
 * subscriber. Carries `PartOps.invokeEvent` for the UI to click
 * PartModule buttons (Deploy, Toggle, ...).
 */
export function PartTopic(persistentId: string): Topic<PartWire, PartOps> {
  return topic<PartWire, PartOps>(`part/${persistentId}`);
}
