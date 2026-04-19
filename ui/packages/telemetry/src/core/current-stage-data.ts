/**
 * Current-stage telemetry — powers the `CurrentStage` panel that
 * sits above `Propulsion` in the left staging stack. Shape mirrors
 * the KSP server's `currentStage` topic wire format.
 *
 * Engines active in the current stage are grouped by the set of
 * tanks they draw from and the propellants they consume (the
 * server resolves this via `Part.crossfeedPartSet` so the grouping
 * is exact, not a heuristic). Each group carries its engine-ID
 * list (for highlighting in the reused engine rosette) and its
 * per-propellant totals summed across the group's tank union.
 */

/** All public frame types are `readonly` on every field — the telemetry
 *  pipeline must produce fresh frames per tick rather than mutating a
 *  scratch object, since in-place mutation doesn't reliably notify
 *  Svelte's `$state` proxy. Defensive cloning is not viable in a
 *  reactive system; the type system is the enforcement mechanism. */

export interface FuelLevel {
  /** KSP resource name as the server emits it: "LiquidFuel",
   *  "Oxidizer", "MonoPropellant", etc. The UI maps to an
   *  abbreviation via `resource-names.ts`. */
  readonly resourceName: string;
  readonly available: number;
  readonly capacity: number;
}

export interface EngineGroup {
  /** Engine IDs — match the `id` field on EngineTopic points. */
  readonly engineIds: readonly string[];
  /** One entry per propellant the group's engines consume. */
  readonly propellants: readonly FuelLevel[];
}

export interface CurrentStageData {
  /** KSP's stage index. Lower numbers = later stages. */
  readonly stageIdx: number;
  /** Stage remaining Δv, m/s, atmosphere-corrected. */
  readonly deltaVStage: number;
  /** Stage thrust-to-weight ratio at current conditions. */
  readonly twrStage: number;
  readonly groups: readonly EngineGroup[];
}
