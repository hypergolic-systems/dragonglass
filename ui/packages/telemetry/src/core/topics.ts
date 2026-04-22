import { topic, type Topic } from './ksp';
import type { ClockData } from './clock-data';
import type { GameData } from './game-data';
import type { FlightData } from './flight-data';
import type { FlightOps } from './flight-ops';
import type { AssemblyModel } from './assembly';
import type { EngineData } from './engine-data';
import type { StageData } from './stage-data';
import type { StageOps } from './stage-ops';
import type { PartData, PawEvent } from './part-data';

export const ClockTopic = topic<ClockData>('clock');
export const GameTopic = topic<GameData>('game');
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
 * Per-part telemetry topic. The topic name embeds the part's KSP
 * persistentId so multiple open PAWs subscribe independently; the
 * server only maintains the frame stream for parts with at least one
 * subscriber.
 */
export function PartTopic(persistentId: string): Topic<PartData> {
  return topic<PartData>(`part/${persistentId}`);
}
