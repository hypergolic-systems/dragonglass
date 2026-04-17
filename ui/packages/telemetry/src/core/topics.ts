import { topic } from './ksp';
import type { FlightData } from './flight-data';
import type { AssemblyModel } from './assembly';

export const FlightTopic = topic<FlightData>('flight');
export const AssemblyTopic = topic<AssemblyModel>('assembly');
