const groupInt = (v: number) => Math.round(v).toLocaleString('en-US');

export function formatSurfaceSpeed(v: number): { value: string; unit: string } {
  if (v >= 1e8) return { value: groupInt(v / 1e6), unit: 'Mm/s' };
  if (v >= 1e7) return { value: (v / 1e6).toFixed(1), unit: 'Mm/s' };
  if (v >= 100000) return { value: groupInt(v / 1000), unit: 'km/s' };
  if (v >= 10000) return { value: (v / 1000).toFixed(1), unit: 'km/s' };
  return { value: groupInt(v), unit: 'm/s' };
}

export function formatAltitude(v: number): { value: string; unit: string } {
  if (v >= 1e10) return { value: groupInt(v / 1e9), unit: 'Gm' };
  if (v >= 1e9) return { value: (v / 1e9).toFixed(1), unit: 'Gm' };
  if (v >= 1e7) return { value: groupInt(v / 1e6), unit: 'Mm' };
  if (v >= 1e6) return { value: (v / 1e6).toFixed(1), unit: 'Mm' };
  if (v >= 10000) return { value: (v / 1000).toFixed(1), unit: 'km' };
  return { value: groupInt(v), unit: 'm' };
}

// Tick labels need to stay short at every magnitude since the tape
// cramps them between the cursor cutout and the panel edge. Use
// suffixes instead of a separate unit field.
export function formatAltLabel(v: number): string {
  if (v === 0) return '0';
  if (v < 1000) return String(v);
  if (v < 1e6) return `${v / 1000}k`;
  if (v < 1e9) return `${v / 1e6}Mm`;
  return `${v / 1e9}Gm`;
}

export function formatSpeedLabel(v: number): string {
  if (v === 0) return '0';
  if (v < 1000) return String(v);
  if (v < 1e6) return `${v / 1000}k`;
  return `${v / 1e6}M`;
}

// Δv readout. Always integer m/s with thousands separators —
// the panel drops the unit label entirely, so the value is
// unambiguously "metres per second" without needing a km/s
// branch that would hide the true scale under unit suffixing.
export function formatDeltaV(v: number): { value: string; unit: string } {
  if (!Number.isFinite(v) || v <= 0) return { value: '—', unit: 'm/s' };
  return { value: Math.round(v).toLocaleString('en-US'), unit: 'm/s' };
}

// Thrust-to-weight ratio. Two decimals below 10, one decimal above;
// dash placeholder when unavailable (e.g. no VesselDeltaV yet).
export function formatTwr(v: number): string {
  if (!Number.isFinite(v) || v <= 0) return '—';
  if (v >= 100) return String(Math.round(v));
  if (v >= 10) return v.toFixed(1);
  return v.toFixed(2);
}

// Instantaneous thrust (kN). Rolls to MN at the four-digit mark.
export function formatThrust(v: number): { value: string; unit: string } {
  if (!Number.isFinite(v) || v < 0) return { value: '—', unit: 'kN' };
  if (v >= 10000) return { value: String(Math.round(v / 1000)), unit: 'MN' };
  if (v >= 1000) return { value: (v / 1000).toFixed(1), unit: 'MN' };
  if (v >= 100) return { value: v.toFixed(0), unit: 'kN' };
  if (v >= 10) return { value: v.toFixed(1), unit: 'kN' };
  if (v > 0) return { value: v.toFixed(2), unit: 'kN' };
  return { value: '0', unit: 'kN' };
}
