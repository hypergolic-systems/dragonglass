/**
 * VAB/SPH parts catalog. Dragonglass replaces the stock parts panel
 * with its own; `PartCatalogTopic` is the server's one-shot emission
 * of every loaded part on editor entry.
 *
 * `category` mirrors KSP's own `PartCategories` enum (strings so they
 * don't drift across KSP versions). `techRequired` is the career-tree
 * node id — consumers can match against the tech list to grey out
 * locked parts, or ignore it in sandbox mode.
 */
export type PartCategory =
  | 'Pods'
  | 'Propulsion'
  | 'Engine'
  | 'FuelTank'
  | 'Control'
  | 'Structural'
  | 'Aero'
  | 'Utility'
  | 'Science'
  | 'Communication'
  | 'Electrical'
  | 'Ground'
  | 'Thermal'
  | 'Payload'
  | 'Coupling'
  | 'Cargo'
  | 'Robotics';

/** Wire-shape PartCategories enum index — stock uses these ints in
 *  the .cfg for part filtering. The decoder maps to the string
 *  variant above before the UI sees it. */
export const CATEGORY_BY_INDEX: readonly PartCategory[] = [
  'Propulsion',
  'Control',
  'Structural',
  'Aero',
  'Utility',
  'Science',
  'Pods',
  'FuelTank',
  'Engine',
  'Communication',
  'Electrical',
  'Ground',
  'Thermal',
  'Payload',
  'Coupling',
  'Cargo',
  'Robotics',
];

export interface PartCatalogEntry {
  /** Stock internal id — `liquidEngine1`, `fuelTank1-2`. Used as the
   *  `pickPart(name)` op key when the player clicks an entry. */
  readonly name: string;
  /** Localized display name. */
  readonly title: string;
  readonly category: PartCategory;
  readonly manufacturer: string;
  /** Funds cost in career mode. 0 when the part is free. */
  readonly cost: number;
  /** Mass in tonnes (from `partPrefab.mass`). */
  readonly mass: number;
  /** Localized flavour description, multi-sentence. */
  readonly description: string;
  /** Career-tree node id gating this part, or '' if it's available
   *  at Tier 0 / no tech requirement. */
  readonly techRequired: string;
  /** Localized search tags separated by stock's own delimiter. */
  readonly tags: string;
  /** Base64-encoded PNG of the part's render, captured server-side
   *  from `AvailablePart.iconPrefab`. Empty string when the capture
   *  failed (missing prefab, bad layer). Clients compose
   *  `data:image/png;base64,${iconBase64}` for an `<img>` src. */
  readonly iconBase64: string;
  /** Comma-separated stock attach-node profile ids — `"size0"`,
   *  `"size1,srf"`, `"mk2,mk3"`. Parts with no stack nodes emit
   *  empty string. Used to filter the catalog by current node
   *  context (or a size chip in the UI). */
  readonly bulkheadProfiles: string;
}

export interface PartCatalogData {
  readonly entries: readonly PartCatalogEntry[];
}

/**
 * Ops the client sends back through `PartCatalogTopic`.
 */
export interface PartCatalogOps {
  /**
   * Editor-only. Picks up the named part and attaches it to the
   * mouse cursor (stock's `EditorLogic.SpawnPart`). Stock's own
   * placement logic handles the rest — the player moves the cursor
   * into the 3D viewport and clicks to drop onto an attach node.
   *
   * `partName` is the stock internal id (`liquidEngine1`, etc.) —
   * the `name` field on `PartCatalogEntry`, NOT its localized title.
   * Unknown names are dropped server-side with a log line.
   */
  pickPart(partName: string): void;

  /**
   * Editor-only. Discards whatever part is currently attached to
   * the cursor, mirroring KSP's drop-on-the-parts-bin gesture
   * (`EditorLogic.DestroySelectedPart`). Idempotent: a no-op when
   * the cursor is empty. The UI should gate this on
   * `EditorStateTopic.heldPart !== null` before firing so clicks
   * without a held part don't send dead traffic.
   */
  deleteHeld(): void;
}
