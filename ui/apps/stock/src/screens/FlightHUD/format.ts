export function formatSurfaceSpeed(v: number): { value: string; unit: string } {
  if (v >= 1e8) return { value: String(Math.round(v / 1e6)), unit: 'Mm/s' };
  if (v >= 1e7) return { value: (v / 1e6).toFixed(1), unit: 'Mm/s' };
  if (v >= 100000) return { value: String(Math.round(v / 1000)), unit: 'km/s' };
  if (v >= 10000) return { value: (v / 1000).toFixed(1), unit: 'km/s' };
  return { value: String(Math.round(v)), unit: 'm/s' };
}

export function formatAltitude(v: number): { value: string; unit: string } {
  if (v >= 1e10) return { value: String(Math.round(v / 1e9)), unit: 'Gm' };
  if (v >= 1e9) return { value: (v / 1e9).toFixed(1), unit: 'Gm' };
  if (v >= 1e7) return { value: String(Math.round(v / 1e6)), unit: 'Mm' };
  if (v >= 1e6) return { value: (v / 1e6).toFixed(1), unit: 'Mm' };
  if (v >= 10000) return { value: String(Math.round(v / 1000)), unit: 'km' };
  return { value: String(Math.round(v)), unit: 'm' };
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
