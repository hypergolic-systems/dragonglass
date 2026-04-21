/**
 * Per-stage telemetry for the active vessel. Shape mirrors the KSP
 * server's `stage` topic wire format — one `StageEntry` per operating
 * stage, ordered as `VesselDeltaV.OperatingStageInfo` presents them.
 *
 * Covers every stage the vessel has, including coast / engineless
 * stages with 0 Δv — the UI keeps them in the stack so the pilot
 * sees the full sequence, not just the burning stages.
 *
 * `stageNum` matches KSP's own numbering: lower is later in flight.
 * Stage 0 is the final (innermost) stage; the highest stageNum is the
 * current booster. Consumers cross-reference `currentStageIdx` to pick
 * out the active stage card.
 */

export type StagingPartKind =
  | 'engine'
  | 'decoupler'
  | 'parachute'
  | 'clamp'
  | 'other';

/**
 * One icon-worthy part in a stage. `kind` is for UI interaction (hover,
 * drag-between-stages, part-focus); `iconName` drives the glyph. The
 * two are deliberately decoupled — an SRB is `kind: 'engine'`, `iconName:
 * 'SOLID_BOOSTER'`, for instance.
 */
export interface StagingPart {
  /** Semantic category. `'other'` is a catch-all for icon-bearing parts
   *  that don't match the primary classification modules (e.g. staged
   *  fuel dumps, utility separators). */
  readonly kind: StagingPartKind;
  /** KSP `Part.persistentId` as a decimal string. Stable across
   *  save/load and the canonical identifier the UI uses to correlate
   *  hover / drag events back to KSP parts via future ops. */
  readonly persistentId: string;
  /** Uppercase `DefaultIcons` enum name from KSP's `Part.stagingIcon`
   *  (e.g. `"LIQUID_ENGINE"`, `"DECOUPLER_VERT"`, `"PARACHUTES"`).
   *  `StagingIcon.svelte` renders a glyph per known name with a
   *  generic fallback for unrecognised values. */
  readonly iconName: string;
  /** persistentIds of physically-symmetric cousins that share this
   *  same stage with the representative. Empty for singletons /
   *  parts whose symmetry cousins have been scattered elsewhere.
   *
   *  Consumers derive the multiplicity badge as
   *  `cousinsInStage.length + 1`. When the user toggles "Ungroup"
   *  in the UI, each id in this list is promoted to its own icon
   *  (client-only — the server state is unchanged). */
  readonly cousinsInStage: readonly string[];
}

export interface StageEntry {
  /** KSP stage number. Lower = later in flight; stage 0 is the
   *  innermost / final. */
  readonly stageNum: number;
  /** Δv available at current atmospheric conditions (m/s). May be 0
   *  for coast / engineless stages. */
  readonly deltaVActual: number;
  /** Thrust-to-weight ratio at current conditions. 0 for stages
   *  that produce no thrust. */
  readonly twrActual: number;
  /** Parts that activate / separate on this stage, pre-sorted server-
   *  side by kind priority (decoupler → engine → parachute → clamp →
   *  other) then persistentId — so render order is deterministic and
   *  stable across frames. */
  readonly parts: readonly StagingPart[];
}

export interface StageData {
  readonly vesselId: string;
  /** The currently active stage per KSP (`Vessel.currentStage`). Use
   *  this to pick out the active card from `stages`; it is NOT
   *  guaranteed to equal the first or last entry since
   *  `OperatingStageInfo` can include earlier / later entries depending
   *  on the vessel's construction. */
  readonly currentStageIdx: number;
  /** One entry per operating stage. Server preserves
   *  `OperatingStageInfo` order. Consumers that want a particular
   *  visual order (e.g. current at the bottom, future stages stacking
   *  upward) should sort client-side. */
  readonly stages: readonly StageEntry[];
}
