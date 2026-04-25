/**
 * Per-engine telemetry for the active vessel. Shape mirrors the KSP
 * server's `engines` topic wire format.
 *
 * Each `EnginePoint` carries:
 *  - A body-local position in the vessel's XZ plane (meters) — the
 *    plane perpendicular to the up-stack axis, i.e. the bottom-up
 *    orthographic "engine map" orientation. The UI treats `y` as its
 *    vertical axis for the 2D map. Positions are stable within a
 *    rigid vessel; they only change on staging, docking, or
 *    structural failure.
 *  - The engine's status byte (burning / flameout / idle / etc.),
 *    configured max thrust (for bubble-chart sizing), and current
 *    atmo-adjusted Isp.
 *  - The propellant-agnostic crossfeed reach — flightIDs of every
 *    part the engine can draw any resource from — and a per-
 *    propellant breakdown with the pooled amount/capacity summed
 *    across the tanks feeding the engine for that propellant.
 *
 * The crossfeed and propellant fields are what the UI's grouping
 * pass consumes: engines with identical crossfeed sets + identical
 * propellant sets belong to the same fuel group.
 *
 * The server dead-zones sub-centimetre position jitter and sub-0.5%
 * fuel-fraction jitter, so a change here almost always means
 * something meaningful moved or drained.
 */

export type EngineStatus =
  | 'burning'
  | 'idle'
  | 'flameout'
  | 'failed'
  | 'shutdown';

export interface EnginePropellant {
  /** KSP resource name, as emitted by the C# side: "LiquidFuel",
   *  "Oxidizer", "MonoPropellant", etc. */
  readonly resourceName: string;
  /** Short display abbreviation from KSP's own
   *  `PartResourceDefinition.abbreviation` — authoritative, no
   *  client-side mapping needed. */
  readonly abbr: string;
  /** Current amount summed across the tanks feeding this engine
   *  for this propellant. */
  readonly available: number;
  /** Capacity summed across the same tanks. */
  readonly capacity: number;
}

export interface EnginePoint {
  /** Stable per-engine id. Stringified KSP `Part.flightID`. */
  readonly id: string;
  /** Body-local X offset from the vessel root, meters (starboard+). */
  readonly x: number;
  /** Body-local Z offset from the vessel root, meters (forward+). Used
   *  as the UI's "up" axis when drawing the 2D engine map. */
  readonly y: number;
  readonly status: EngineStatus;
  /** Per-engine post-everything throttle, 0..1. Already accounts for
   *  the vessel's main throttle, the per-engine thrust limiter, and
   *  the engine's response curves. The C# side forces this to 0
   *  unless `status === 'burning'`, so flamed-out / shutdown engines
   *  never carry a stale commanded value. */
  readonly throttle: number;
  /** Configured maximum thrust (kN, vacuum). Stable across flight.
   *  The engine map uses this to size each circle so area encodes
   *  thrust magnitude. */
  readonly maxThrust: number;
  /** Current atmosphere-adjusted Isp, seconds. Sourced from the
   *  engine's stock atmosphereCurve at the vessel's current static
   *  pressure; valid whether the engine is lit or not. */
  readonly isp: number;
  /** Sorted flightIDs of every part in the engine's crossfeed reach.
   *  Propellant-agnostic — matches KSP's own crossfeedPartSet. The
   *  UI uses this (plus the propellant set below) as the engine's
   *  grouping signature. */
  readonly crossfeedPartIds: readonly string[];
  /** One entry per propellant the engine consumes, sorted by KSP
   *  resource id (so two engines with identical propellants in
   *  different declared order produce identical arrays). */
  readonly propellants: readonly EnginePropellant[];
}

export interface EngineData {
  readonly vesselId: string;
  readonly engines: readonly EnginePoint[];
}

/**
 * Fuel-group partitioning — the UI computes this by partitioning
 * `EnginePoint[]` on crossfeed signature (see `engine-groups.ts` in
 * the stock app). Two engines belong to the same group iff they
 * draw from the same crossfeed set for the same propellants.
 *
 * `propellants` here carries the group's summed totals across its
 * engines' tank unions — the grouping pass copies these straight
 * off any member (they're identical by construction within a
 * group).
 */
export interface EngineGroup {
  /** Engine IDs — match the `id` field on `EnginePoint`. */
  readonly engineIds: readonly string[];
  /** One entry per propellant the group's engines consume. */
  readonly propellants: readonly EnginePropellant[];
}
