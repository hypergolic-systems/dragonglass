/**
 * Wall-clock telemetry: universal time (UT) and mission elapsed time
 * (MET). UT always flows when physics is advancing; MET is only
 * defined while an active vessel exists.
 */
export interface ClockData {
  ut: number;          // seconds since game start
  met: number | null;  // seconds since vessel launch, null outside flight
}
