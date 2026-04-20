import { topic } from './ksp';
import type { ClockData } from './clock-data';
import type { GameData } from './game-data';
import type { FlightData } from './flight-data';
import type { FlightOps } from './flight-ops';
import type { AssemblyModel } from './assembly';
import type { EngineData } from './engine-data';

export const ClockTopic = topic<ClockData>('clock');
export const GameTopic = topic<GameData>('game');
export const FlightTopic = topic<FlightData, FlightOps>('flight');
export const AssemblyTopic = topic<AssemblyModel>('assembly');
export const EngineTopic = topic<EngineData>('engines');
