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
}

export interface PartCatalogData {
  readonly entries: readonly PartCatalogEntry[];
}
