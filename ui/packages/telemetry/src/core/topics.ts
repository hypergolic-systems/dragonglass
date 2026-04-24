import { topic, type Topic } from './ksp';
import type { ClockData } from './clock-data';
import type { GameData } from './game-data';
import type { GameOps } from './game-ops';
import type { FlightData } from './flight-data';
import type { FlightOps } from './flight-ops';
import type { AssemblyModel } from './assembly';
import type { EngineData } from './engine-data';
import type { StageData } from './stage-data';
import type { StageOps } from './stage-ops';
import type { PartData, PawEvent, PartOps } from './part-data';
import type { PartCatalogData, PartCatalogOps } from './part-catalog-data';

export const ClockTopic = topic<ClockData>('clock');
export const GameTopic = topic<GameData, GameOps>('game');
export const FlightTopic = topic<FlightData, FlightOps>('flight');
export const AssemblyTopic = topic<AssemblyModel>('assembly');
export const EngineTopic = topic<EngineData>('engines');
export const StageTopic = topic<StageData, StageOps>('stage');

/**
 * PAW open-intent pulses (persistentId of the part the pilot
 * right-clicked). Treat each dispatch as an event, not a state — see
 * `part-data.ts`.
 */
export const PawTopic = topic<PawEvent>('paw');

/**
 * VAB/SPH parts catalog. One-shot emission on editor entry; the
 * server doesn't re-emit mid-session because loaded parts don't
 * change at runtime. Consumers should cache the first frame and
 * reuse it for the duration of the editor scene.
 */
export const PartCatalogTopic = topic<PartCatalogData, PartCatalogOps>('partCatalog');

/**
 * Per-part telemetry topic. The topic name embeds the part's KSP
 * persistentId so multiple open PAWs subscribe independently; the
 * server only maintains the frame stream for parts with at least one
 * subscriber. Carries `PartOps.invokeEvent` for the UI to click
 * PartModule buttons (Deploy, Toggle, ...).
 */
export function PartTopic(persistentId: string): Topic<PartData, PartOps> {
  return topic<PartData, PartOps>(`part/${persistentId}`);
}
