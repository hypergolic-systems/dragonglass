/**
 * Per-engine telemetry for the active vessel. Shape mirrors the KSP
 * server's `engines` topic wire format.
 *
 * Each `EnginePoint` carries a body-local position in the vessel's
 * XZ plane (meters) — the plane perpendicular to the up-stack axis,
 * i.e. the bottom-up orthographic "engine map" orientation. The UI
 * treats `y` as its vertical axis for the 2D map.
 *
 * Positions are stable within a rigid vessel; they only change on
 * staging, docking, or structural failure. The server dead-zones
 * sub-centimetre jitter, so a change here almost always means
 * something meaningful moved.
 */

export type EngineStatus = 'burning' | 'flameout' | 'failed' | 'shutdown';

export interface EnginePoint {
  /** Stable per-engine id. Stringified KSP `Part.flightID`. */
  readonly id: string;
  /** Body-local X offset from the vessel root, meters (starboard+). */
  readonly x: number;
  /** Body-local Z offset from the vessel root, meters (forward+). Used
   *  as the UI's "up" axis when drawing the 2D engine map. */
  readonly y: number;
  readonly status: EngineStatus;
  /** Configured maximum thrust (kN, vacuum). Stable across flight.
   *  The engine map uses this to size each circle so area encodes
   *  thrust magnitude. */
  readonly maxThrust: number;
}

export interface EngineData {
  readonly vesselId: string;
  readonly engines: readonly EnginePoint[];
}
