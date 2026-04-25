// Presentation helper for fuel-group gauge labels. The per-
// propellant `abbr` now arrives on the wire (sourced from stock
// KSP's `PartResourceDefinition.abbreviation`), so there's no
// resource-name table to maintain here.
//
// When a group's propellants drain in matched proportion we render
// a single "unified" bar. Its label concatenates each propellant's
// abbreviation, with a special case for the LF+Ox pair which has
// a well-known combined shorthand ("LFO").

export function unifiedResourceLabel(abbrs: readonly string[]): string {
  const sorted = [...abbrs].sort();
  const key = sorted.join('+').toUpperCase();
  if (key === 'LF+OX') return 'LFO';
  return abbrs.join('/');
}
