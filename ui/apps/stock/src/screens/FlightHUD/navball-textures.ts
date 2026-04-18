export type MarkerKind =
  | 'prograde'
  | 'retrograde'
  | 'radial-out'
  | 'radial-in'
  | 'normal'
  | 'anti-normal'
  | 'target-prograde'
  | 'target-retrograde';

export const MARKER_KINDS: readonly MarkerKind[] = [
  'prograde',
  'retrograde',
  'radial-out',
  'radial-in',
  'normal',
  'anti-normal',
  'target-prograde',
  'target-retrograde',
] as const;

export const MARKER_COLOR: Record<MarkerKind, string> = {
  prograde:            '#ffd95a',
  retrograde:          '#ffd95a',
  'radial-out':        '#70dfff',
  'radial-in':         '#70dfff',
  normal:              '#c68cff',
  'anti-normal':       '#c68cff',
  'target-prograde':   '#ff72d6',
  'target-retrograde': '#ff72d6',
};

export function drawNavballTexture(): HTMLCanvasElement {
  const W = 2048;
  const H = 1024;
  const canvas = document.createElement('canvas');
  canvas.width = W;
  canvas.height = H;
  const ctx = canvas.getContext('2d')!;
  const eq = H / 2;

  // Sky at canvas top, ground at canvas bottom. The three.js default
  // flipY=true maps canvas y=0 to UV.y=1 → sphere +Y pole, so sky
  // lands at the zenith.
  const sky = ctx.createLinearGradient(0, 0, 0, eq);
  sky.addColorStop(0, '#081a30');
  sky.addColorStop(0.55, '#143758');
  sky.addColorStop(1, '#3b79a8');
  ctx.fillStyle = sky;
  ctx.fillRect(0, 0, W, eq);

  const ground = ctx.createLinearGradient(0, eq, 0, H);
  ground.addColorStop(0, '#b46332');
  ground.addColorStop(0.5, '#6a331a');
  ground.addColorStop(1, '#1a0a04');
  ctx.fillStyle = ground;
  ctx.fillRect(0, eq, W, H - eq);

  // Horizon line
  ctx.strokeStyle = '#f2f7fc';
  ctx.lineWidth = 7;
  ctx.beginPath();
  ctx.moveTo(0, eq);
  ctx.lineTo(W, eq);
  ctx.stroke();

  // Parallels every 10° elevation (pitch)
  for (let el = -80; el <= 80; el += 10) {
    if (el === 0) continue;
    const y = eq - (el / 90) * eq;
    const major = el % 30 === 0;
    ctx.strokeStyle =
      el > 0 ? 'rgba(232, 244, 255, 0.55)' : 'rgba(248, 224, 196, 0.55)';
    ctx.lineWidth = major ? 2.8 : 1.3;
    if (!major) ctx.setLineDash([14, 10]);
    ctx.beginPath();
    ctx.moveTo(0, y);
    ctx.lineTo(W, y);
    ctx.stroke();
    ctx.setLineDash([]);
  }

  // Meridians every 10° longitude
  for (let lon = 0; lon < 360; lon += 10) {
    const x = (lon / 360) * W;
    const isCardinal = lon % 90 === 0;
    const major = lon % 30 === 0;
    ctx.strokeStyle = isCardinal
      ? 'rgba(255, 255, 255, 0.78)'
      : major
      ? 'rgba(236, 244, 255, 0.45)'
      : 'rgba(236, 244, 255, 0.2)';
    ctx.lineWidth = isCardinal ? 3.2 : major ? 1.8 : 0.9;
    if (!major) ctx.setLineDash([10, 14]);
    ctx.beginPath();
    ctx.moveTo(x, 0);
    ctx.lineTo(x, H);
    ctx.stroke();
    ctx.setLineDash([]);
  }

  // Cardinal letters (big)
  ctx.textAlign = 'center';
  ctx.textBaseline = 'middle';
  ctx.font = '700 64px "Azeret Mono", ui-monospace, monospace';
  // On a three.js SphereGeometry, sphere-local +Z (U=0.25 at the
  // equator) is what ends up at the crosshair when the vessel faces
  // north in the surface frame — so the "N" label lives at U=0.25,
  // not U=0. Shifting all four cardinals by -0.25 in U gives:
  //   U=0    → W     (was N)
  //   U=0.25 → N     (was E)
  //   U=0.5  → E     (was S)
  //   U=0.75 → S     (was W)
  const cardinals: { u: number; label: string }[] = [
    { u: 0, label: 'W' },
    { u: 0.25, label: 'N' },
    { u: 0.5, label: 'E' },
    { u: 0.75, label: 'S' },
  ];
  // Draw each glyph three times (x-W, x, x+W) so labels positioned
  // on the U=0 seam stitch cleanly. At u=0 a naive center-aligned
  // fillText clips its left half against the canvas edge; the +W
  // copy lands near the right edge and, because the sphere texture
  // wraps S, completes the glyph across the seam.
  const drawWrapped = (label: string, x: number, y: number) => {
    ctx.fillText(label, x - W, y);
    ctx.fillText(label, x, y);
    ctx.fillText(label, x + W, y);
  };
  cardinals.forEach(({ u, label }) => {
    const x = u * W;
    ctx.fillStyle = '#f3f9ff';
    drawWrapped(label, x, eq - 48);
    ctx.fillStyle = '#fbe9d4';
    drawWrapped(label, x, eq + 48);
  });

  // Intermediate heading numbers every 30°
  ctx.font = '600 38px "Azeret Mono", ui-monospace, monospace';
  for (let lon = 30; lon < 360; lon += 30) {
    if (lon % 90 === 0) continue;
    const x = (lon / 360) * W;
    const label = (lon / 10).toString();
    ctx.fillStyle = 'rgba(234, 244, 255, 0.78)';
    ctx.fillText(label, x, eq - 44);
    ctx.fillStyle = 'rgba(250, 232, 208, 0.78)';
    ctx.fillText(label, x, eq + 44);
  }

  // Pitch labels at major parallels
  ctx.font = '700 30px "Azeret Mono", ui-monospace, monospace';
  for (let el = -60; el <= 60; el += 30) {
    if (el === 0) continue;
    const y = eq - (el / 90) * eq;
    const label = Math.abs(el).toString();
    ctx.fillStyle =
      el > 0 ? 'rgba(232, 244, 255, 0.85)' : 'rgba(248, 224, 196, 0.85)';
    [0, 0.25, 0.5, 0.75].forEach((u) => {
      const x = u * W;
      // Same seam-wrap trick as the cardinals: the u=0 labels sit
      // right on the seam, so draw each pair at x±W too.
      drawWrapped(label, x - 60, y);
      drawWrapped(label, x + 60, y);
    });
  }

  // Pole markers.
  ctx.font = '700 32px "Azeret Mono", ui-monospace, monospace';
  ctx.fillStyle = 'rgba(232, 244, 255, 0.75)';
  ctx.fillText('ZENITH', W / 2, 48);
  ctx.fillStyle = 'rgba(248, 224, 196, 0.75)';
  ctx.fillText('NADIR', W / 2, H - 48);

  return canvas;
}

export function drawMarkerTexture(kind: MarkerKind): HTMLCanvasElement {
  const S = 128;
  const canvas = document.createElement('canvas');
  canvas.width = S;
  canvas.height = S;
  const ctx = canvas.getContext('2d')!;
  const cx = S / 2;
  const cy = S / 2;
  const r = S * 0.3;
  const color = MARKER_COLOR[kind];

  ctx.strokeStyle = color;
  ctx.fillStyle = color;
  ctx.lineCap = 'round';
  ctx.lineJoin = 'round';
  ctx.shadowColor = color;
  ctx.shadowBlur = 10;

  const ring = (lw = 5) => {
    ctx.lineWidth = lw;
    ctx.beginPath();
    ctx.arc(cx, cy, r, 0, Math.PI * 2);
    ctx.stroke();
  };

  const stubsAt = (angles: number[], len = 14) => {
    ctx.lineWidth = 5;
    for (const a of angles) {
      ctx.beginPath();
      ctx.moveTo(cx + Math.cos(a) * r, cy + Math.sin(a) * r);
      ctx.lineTo(cx + Math.cos(a) * (r + len), cy + Math.sin(a) * (r + len));
      ctx.stroke();
    }
  };

  const dot = (rad: number) => {
    ctx.beginPath();
    ctx.arc(cx, cy, rad, 0, Math.PI * 2);
    ctx.fill();
  };

  switch (kind) {
    case 'prograde': {
      ring();
      dot(4);
      stubsAt([0, Math.PI, -Math.PI / 2]);
      break;
    }
    case 'retrograde': {
      ring();
      stubsAt([0, Math.PI / 2, Math.PI, -Math.PI / 2]);
      ctx.lineWidth = 6;
      const d = r * 0.6;
      ctx.beginPath();
      ctx.moveTo(cx - d, cy - d);
      ctx.lineTo(cx + d, cy + d);
      ctx.moveTo(cx + d, cy - d);
      ctx.lineTo(cx - d, cy + d);
      ctx.stroke();
      break;
    }
    case 'radial-out': {
      ring();
      dot(4);
      for (let q = 0; q < 4; q++) {
        const a = (q * Math.PI) / 2;
        const tipX = cx + Math.cos(a) * (r + 18);
        const tipY = cy + Math.sin(a) * (r + 18);
        const lX = cx + Math.cos(a - 0.22) * (r + 2);
        const lY = cy + Math.sin(a - 0.22) * (r + 2);
        const rX = cx + Math.cos(a + 0.22) * (r + 2);
        const rY = cy + Math.sin(a + 0.22) * (r + 2);
        ctx.beginPath();
        ctx.moveTo(tipX, tipY);
        ctx.lineTo(lX, lY);
        ctx.lineTo(rX, rY);
        ctx.closePath();
        ctx.fill();
      }
      break;
    }
    case 'radial-in': {
      ring();
      ctx.lineWidth = 4;
      const d = r * 0.32;
      ctx.beginPath();
      ctx.moveTo(cx - d, cy);
      ctx.lineTo(cx + d, cy);
      ctx.moveTo(cx, cy - d);
      ctx.lineTo(cx, cy + d);
      ctx.stroke();
      for (let q = 0; q < 4; q++) {
        const a = (q * Math.PI) / 2;
        const tipX = cx + Math.cos(a) * (r + 2);
        const tipY = cy + Math.sin(a) * (r + 2);
        const lX = cx + Math.cos(a - 0.2) * (r + 20);
        const lY = cy + Math.sin(a - 0.2) * (r + 20);
        const rX = cx + Math.cos(a + 0.2) * (r + 20);
        const rY = cy + Math.sin(a + 0.2) * (r + 20);
        ctx.beginPath();
        ctx.moveTo(tipX, tipY);
        ctx.lineTo(lX, lY);
        ctx.lineTo(rX, rY);
        ctx.closePath();
        ctx.fill();
      }
      break;
    }
    case 'normal': {
      ctx.lineWidth = 5;
      const h = r * 1.2;
      const w = r * 1.1;
      ctx.beginPath();
      ctx.moveTo(cx, cy - h);
      ctx.lineTo(cx - w, cy + h * 0.5);
      ctx.lineTo(cx + w, cy + h * 0.5);
      ctx.closePath();
      ctx.stroke();
      ctx.beginPath();
      ctx.arc(cx, cy + h * 0.08, 4, 0, Math.PI * 2);
      ctx.fill();
      break;
    }
    case 'anti-normal': {
      ctx.lineWidth = 5;
      const h = r * 1.2;
      const w = r * 1.1;
      ctx.beginPath();
      ctx.moveTo(cx, cy + h);
      ctx.lineTo(cx - w, cy - h * 0.5);
      ctx.lineTo(cx + w, cy - h * 0.5);
      ctx.closePath();
      ctx.stroke();
      ctx.beginPath();
      ctx.arc(cx, cy - h * 0.08, 4, 0, Math.PI * 2);
      ctx.fill();
      break;
    }
    case 'target-prograde': {
      // Square + dot, rotated 45° to read as a diamond — matches
      // stock KSP's target-prograde glyph.
      ctx.lineWidth = 5;
      const d = r * 0.95;
      ctx.beginPath();
      ctx.moveTo(cx, cy - d);
      ctx.lineTo(cx + d, cy);
      ctx.lineTo(cx, cy + d);
      ctx.lineTo(cx - d, cy);
      ctx.closePath();
      ctx.stroke();
      dot(4);
      stubsAt([0, Math.PI, -Math.PI / 2]);
      break;
    }
    case 'target-retrograde': {
      ctx.lineWidth = 5;
      const d = r * 0.95;
      ctx.beginPath();
      ctx.moveTo(cx, cy - d);
      ctx.lineTo(cx + d, cy);
      ctx.lineTo(cx, cy + d);
      ctx.lineTo(cx - d, cy);
      ctx.closePath();
      ctx.stroke();
      ctx.lineWidth = 6;
      const x = r * 0.5;
      ctx.beginPath();
      ctx.moveTo(cx - x, cy - x);
      ctx.lineTo(cx + x, cy + x);
      ctx.moveTo(cx + x, cy - x);
      ctx.lineTo(cx - x, cy + x);
      ctx.stroke();
      stubsAt([0, Math.PI / 2, Math.PI, -Math.PI / 2]);
      break;
    }
  }

  return canvas;
}
