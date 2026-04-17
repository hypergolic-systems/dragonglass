export function formatSurfaceSpeed(v: number): { value: string; unit: string } {
  if (v >= 100000) {
    return { value: String(Math.round(v / 1000)), unit: 'km/s' };
  }
  if (v >= 10000) {
    return { value: (v / 1000).toFixed(1), unit: 'km/s' };
  }
  return { value: String(Math.round(v)), unit: 'm/s' };
}

export function formatAltitude(v: number): { value: string; unit: string } {
  if (v >= 10000) {
    return { value: String(Math.round(v / 1000)), unit: 'km' };
  }
  return { value: String(Math.round(v)), unit: 'm' };
}

export const formatAltLabel = (v: number): string =>
  v === 0 ? '0' : `${v / 1000}k`;
