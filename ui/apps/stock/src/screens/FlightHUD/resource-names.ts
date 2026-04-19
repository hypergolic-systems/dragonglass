// Map from KSP resource names (as emitted by the C# side) to the
// short uppercase abbreviations we render on fuel gauges. Stock
// KSP long names crowd the 120-px-wide CurrentStage panel; the
// abbreviations are small-caps-sized labels that sit naturally
// next to a horizontal bar.

export const RESOURCE_ABBREV: Record<string, string> = {
  LiquidFuel: 'LF',
  Oxidizer: 'OX',
  MonoPropellant: 'MONO',
  XenonGas: 'XE',
  SolidFuel: 'SF',
  Ore: 'ORE',
  ElectricCharge: 'EC',
  EVAPropellant: 'EVA',
  IntakeAir: 'AIR',
};

export function shortResourceName(name: string): string {
  return RESOURCE_ABBREV[name] ?? name.toUpperCase().slice(0, 4);
}

// When a group's propellants drain in matched proportion we
// render a single "unified" bar. Its label concatenates each
// propellant's abbreviation, with a couple of special-case pairs
// that have well-known combined names (stock LFO being the
// prominent one).
export function unifiedResourceLabel(names: string[]): string {
  const abbrevs = names.map(shortResourceName);
  const sorted = [...abbrevs].sort();
  const key = sorted.join('+');
  if (key === 'LF+OX') return 'LFO';
  return abbrevs.join('/');
}
