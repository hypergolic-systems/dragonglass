// Layout pass for the Propulsion panel's engine map.
//
// Input: raw engines (body-local XZ meters + status + max thrust)
// from the KSP side. Output: render position + per-engine radius
// in a normalised viewport centered on 0 with extents ±1.
//
// ---------------------------------------------------------------
// Algorithm
// ---------------------------------------------------------------
//
// Real rocket engine layouts are fundamentally *radial* — 1 central
// bell + 8 outer at Falcon 9, 3+10+20 at Starship Super Heavy, 1+4
// at Saturn V. KSP's part placement doesn't quite hit exact radii
// (a small asymmetry here, a 5cm stagger there), and the raw XZ
// coordinates reflect whatever distance from the rocket's axis the
// engines happen to land at — often wasted space in the visual
// plot. We want the panel to read like a stylised blueprint of the
// stack, not a photorealistic pantograph.
//
// Three phases:
//
//   1. Radius per engine. Area ∝ thrust (so r ∝ √thrust), mapped
//      to a [R_MIN, R_MAX] visual range. Unchanged from before.
//
//   2. Ring binning. Sort engines by distance from the centroid;
//      greedy-cluster into rings with a distance tolerance that
//      absorbs small KSP part-position jitter. Each engine lands
//      on exactly one ring with a preferred angle taken from its
//      raw position.
//
//   3. Constrained relaxation. Iterative physics with three forces:
//
//        - weak inward pull on each ring: every ring tries to
//          shrink toward the center one small step per iteration,
//          so the layout compacts toward what the central axis is
//          capable of;
//
//        - strict ring ordering: the outer edge of ring k-1 (its
//          radius plus its largest engine's own radius) must sit
//          inside the inner edge of ring k, with a small pad —
//          rings cannot cross;
//
//        - strict overlap repulsion between engines on the same
//          ring: tangential chord distance at the current ring
//          radius must exceed the sum of the two engines' radii
//          plus pad; if not, we rotate them apart along the ring
//          by the deficit.
//
//      Inner rings shrink until they bottom out on either their
//      own tangential-fit constraint or the ring-ordering floor
//      from an inner neighbour. Outer rings follow suit. A raw
//      `o    O    o` (well-separated big-central-with-side-
//      boosters) idealises to `oOo` tightly packed, while a clean
//      Saturn V `O` central with four outer bells stays as `O`
//      plus a compact outer ring.
//
// Kept pure and side-effect free so it's trivially unit-testable.

import type { EnginePoint, EngineStatus } from '@dragonglass/telemetry/core';

export interface EngineRenderPoint {
  id: string;
  /** Normalised viewport X, in [-1, 1]. */
  cx: number;
  /** Normalised viewport Y. Flipped sign versus raw body-Z so
   *  vessel-forward points up in the UI, matching the SpaceX
   *  bottom-up convention. */
  cy: number;
  /** Circle radius in the same normalised units as cx/cy. */
  r: number;
  status: EngineStatus;
}

export interface EngineMapLayout {
  points: EngineRenderPoint[];
}

// Outer rim — the cluster's outer edge lands exactly here after
// the final fit pass (scales both up and down, so compact layouts
// fill the map rather than leaving dead space).
const OUTER_R = 0.88;

// Cap on any single engine's radius, expressed as a fraction of
// OUTER_R. Without this, a one-engine vessel (or a two-engine
// cluster hugging the center) would scale up until the sole bell
// fills the entire map — visually dominant to the point of
// looking broken. 0.5 keeps the biggest engine at no more than
// half the viewport radius (so diameter ≤ OUTER_R); tight clusters
// still fill the width but sparse layouts leave breathing room.
const MAX_ENGINE_R_FRACTION = 0.5;

// Visual radius range for engine circles. Tuned so:
//   - a Mammoth-sized engine (near R_MAX) reads as a substantial
//     disc against the 2×2 viewport,
//   - a Spider-sized engine (near R_MIN) still renders as a
//     legible circle rather than a pixel speck.
const R_MIN = 0.055;
const R_MAX = 0.14;
// Used when no thrust info is available (stock engine part with
// maxThrust 0, pre-ignition loadout with no VesselDeltaV, etc.).
const R_DEFAULT = 0.1;

// Packing-vs-visual radius ratio. Every engine claims a layout
// radius `LAYOUT_INFLATION × visual radius` for purposes of
// tangential fit and ring ordering, so adjacent circles have a
// guaranteed gap that scales with engine size (≈ 30% of the
// paired mean radius between neighbours). Visual radii stay at
// their true thrust-derived values — only the packing constraint
// uses the inflated form.
const LAYOUT_INFLATION = 1.15;

// Distance tolerance for ring assignment, in normalised units
// (the same space cx / cy live in). Two engines whose distances
// from the centroid differ by less than this land on the same
// ring. 0.12 ≈ one engine-diameter at typical size — enough to
// absorb KSP's placement jitter without merging Falcon-9's inner
// and outer rings.
const RING_TOLERANCE = 0.12;

// Iteration budget for the relaxation phase. Layouts typically
// settle in ~40 passes; 100 leaves headroom for multi-ring stacks.
const MAX_ITER = 100;

// Per-iteration shrink applied to each ring (normalised units).
// Slow shrink gives the overlap and ordering constraints time to
// push back each pass, which produces a clean equilibrium.
const SHRINK_STEP = 0.008;

interface RingState {
  engineIdxs: number[];
  radius: number;
  /** Largest engine radius on this ring. Drives the ring-ordering
   *  constraint on both sides. */
  maxEngineR: number;
}

interface MutablePoint {
  id: string;
  status: EngineStatus;
  r: number;
  ringIdx: number;
  angle: number;
}

export function idealizeEngineMap(engines: readonly EnginePoint[]): EngineMapLayout {
  if (engines.length === 0) return { points: [] };

  // ----- Phase 1: centroid-relative polar coords -----
  let mx = 0;
  let my = 0;
  for (const e of engines) {
    mx += e.x;
    my += e.y;
  }
  mx /= engines.length;
  my /= engines.length;

  // ----- Per-engine radius from max thrust. Area ∝ thrust, so
  //       r ∝ √thrust. Identical thrusts (or missing data) fall
  //       back to R_DEFAULT. -----
  const thrusts = engines.map((e) => Math.max(0, e.maxThrust));
  const haveThrust = thrusts.some((t) => t > 0);
  let sqTMin = Infinity;
  let sqTMax = 0;
  if (haveThrust) {
    for (const t of thrusts) {
      if (t <= 0) continue;
      const s = Math.sqrt(t);
      if (s < sqTMin) sqTMin = s;
      if (s > sqTMax) sqTMax = s;
    }
  }
  const sqSpan = sqTMax - sqTMin;
  const radiusFor = (t: number): number => {
    if (!haveThrust || t <= 0) return R_DEFAULT;
    if (sqSpan < 1e-6) return (R_MIN + R_MAX) / 2;
    const n = (Math.sqrt(t) - sqTMin) / sqSpan;
    return R_MIN + n * (R_MAX - R_MIN);
  };

  // Raw polar components. Flip Y when deriving the UI angle so
  // vessel-forward (+body-Z) lands at the top of the 2D map.
  const rawDists: number[] = new Array(engines.length);
  const rawAngles: number[] = new Array(engines.length);
  let rawMax = 0;
  for (let i = 0; i < engines.length; i++) {
    const dx = engines[i].x - mx;
    const dy = engines[i].y - my;
    const d = Math.hypot(dx, dy);
    rawDists[i] = d;
    rawAngles[i] = Math.atan2(-dy, dx);
    if (d > rawMax) rawMax = d;
  }

  // Initial scale to normalised space. Picks a value that leaves
  // the widest engine inside OUTER_R at the raw layout's farthest
  // point. The final fit at the end of the pipeline scales both
  // up and down to make the actual cluster extent match OUTER_R,
  // so this choice only affects how fast relaxation settles.
  const perR = engines.map((e) => radiusFor(e.maxThrust));
  const maxEngineR = Math.max(...perR);
  const scale =
    rawMax > 1e-6 ? Math.max(0.01, (OUTER_R - maxEngineR) / rawMax) : 1;

  const pts: MutablePoint[] = engines.map((e, i) => ({
    id: e.id,
    status: e.status,
    r: perR[i],
    ringIdx: -1,
    angle: rawAngles[i],
  }));

  // ----- Phase 2: ring binning -----
  //
  // Sort by normalised distance, greedy-cluster under
  // RING_TOLERANCE. Each successive engine either extends the
  // current ring (updating its mean) or starts a new one.
  const sortedIdxs = pts
    .map((_, i) => i)
    .sort((a, b) => rawDists[a] - rawDists[b]);
  const rings: RingState[] = [];
  for (const idx of sortedIdxs) {
    const nd = rawDists[idx] * scale;
    const last = rings[rings.length - 1];
    if (last && nd - last.radius <= RING_TOLERANCE) {
      last.engineIdxs.push(idx);
      // Recompute ring radius as the mean of its members. Keeps
      // the ring centered on the engines' raw distribution rather
      // than anchored to the first one in.
      let sum = 0;
      for (const i of last.engineIdxs) sum += rawDists[i] * scale;
      last.radius = sum / last.engineIdxs.length;
      if (pts[idx].r > last.maxEngineR) last.maxEngineR = pts[idx].r;
    } else {
      rings.push({
        engineIdxs: [idx],
        radius: nd,
        maxEngineR: pts[idx].r,
      });
    }
    pts[idx].ringIdx = rings.length - 1;
  }

  // ----- Phase 3: constrained relaxation -----
  for (let iter = 0; iter < MAX_ITER; iter++) {
    let changed = false;

    // Weak inward pull on every ring. Each ring's floor is
    // whichever is larger of:
    //   (a) its own tangential-fit minimum at the current angles
    //       (see below), or
    //   (b) the ordering constraint enforced in the next pass.
    // The shrink step here is soft — ordering is enforced
    // authoritatively afterwards.
    for (const ring of rings) {
      if (ring.engineIdxs.length === 1) {
        // One-engine ring wants to sit at the center.
        if (ring.radius > 0) {
          ring.radius = Math.max(0, ring.radius - SHRINK_STEP);
          changed = true;
        }
        continue;
      }

      // Tangential-fit floor: the smallest radius at which the
      // current angular distribution fits without same-ring
      // overlap. For each consecutive pair at angle gap `da`, a
      // chord = 2R·sin(da/2) must exceed the sum of the pair's
      // packing radii, so R_required = (rA + rB) / (2·sin(da/2)).
      // Take the worst pair. Packing radii are inflated versus
      // the visual radii via LAYOUT_INFLATION.
      const sortedByAngle = ring.engineIdxs
        .slice()
        .sort((a, b) => pts[a].angle - pts[b].angle);
      let tangMin = 0;
      for (let i = 0; i < sortedByAngle.length; i++) {
        const a = pts[sortedByAngle[i]];
        const b = pts[sortedByAngle[(i + 1) % sortedByAngle.length]];
        let da = b.angle - a.angle;
        if (i === sortedByAngle.length - 1) da += 2 * Math.PI;
        const half = da / 2;
        // Skip both degenerate endpoints — half ≈ 0 means the pair
        // is effectively coincident (handled by the angular push);
        // half ≈ π means the wrap pair on a 2-engine ring has
        // already placed the engines diametrically, or is about to
        // (sin(π) = 0 would otherwise produce Infinity).
        if (half < 1e-6 || half > Math.PI - 1e-6) continue;
        const required = (a.r + b.r) * LAYOUT_INFLATION;
        const rReq = required / (2 * Math.sin(half));
        if (rReq > tangMin) tangMin = rReq;
      }

      if (ring.radius > tangMin + 1e-6) {
        // Shrink slowly — lets the ordering and angular passes
        // push back each step, which produces a clean
        // equilibrium.
        ring.radius = Math.max(tangMin, ring.radius - SHRINK_STEP);
        changed = true;
      } else if (ring.radius < tangMin - 1e-6) {
        // Snap up to the tangential floor. Only triggers in
        // degenerate states (all engines starting at the same
        // angle after a bad scale, etc.); without it the ring
        // gets stuck undersized.
        ring.radius = tangMin;
        changed = true;
      }
    }

    // Strict ring ordering. Outer ring must clear the inner
    // ring's outer edge (radius + its largest engine's packing
    // radius) by this ring's own largest engine's packing radius.
    // Inflation is applied to both side engines, producing a gap
    // that scales with the engines themselves rather than a
    // constant. No iteration — just snap.
    for (let k = 1; k < rings.length; k++) {
      const minR =
        rings[k - 1].radius +
        (rings[k - 1].maxEngineR + rings[k].maxEngineR) * LAYOUT_INFLATION;
      if (rings[k].radius < minR) {
        rings[k].radius = minR;
        changed = true;
      }
    }

    // Angular repulsion on each ring. If neighbouring engines
    // overlap at the ring's current radius, rotate them apart by
    // the angular deficit — half each side so adjacent pairs
    // stay balanced across iterations. The next pass's
    // tangential floor will update accordingly.
    for (const ring of rings) {
      if (ring.engineIdxs.length < 2 || ring.radius < 1e-6) continue;
      const sortedByAngle = ring.engineIdxs
        .slice()
        .sort((a, b) => pts[a].angle - pts[b].angle);
      for (let i = 0; i < sortedByAngle.length; i++) {
        const a = pts[sortedByAngle[i]];
        const b = pts[sortedByAngle[(i + 1) % sortedByAngle.length]];
        let da = b.angle - a.angle;
        if (i === sortedByAngle.length - 1) da += 2 * Math.PI;
        const chord = 2 * ring.radius * Math.sin(da / 2);
        const required = (a.r + b.r) * LAYOUT_INFLATION;
        if (chord < required - 1e-6) {
          // Angle required at the current radius. If the pair's
          // required chord exceeds the ring's diameter, clamp at
          // diametric opposition — the ring itself will expand
          // via the tangential floor next pass.
          const sinTarget = Math.min(1, required / (2 * ring.radius));
          const needDa = 2 * Math.asin(sinTarget);
          const adjust = (needDa - da) / 2;
          a.angle -= adjust;
          b.angle += adjust;
          changed = true;
        }
      }
    }

    if (!changed) break;
  }

  // ----- Emit cartesian positions -----
  const points: EngineRenderPoint[] = pts.map((p) => ({
    id: p.id,
    cx: rings[p.ringIdx].radius * Math.cos(p.angle),
    cy: rings[p.ringIdx].radius * Math.sin(p.angle),
    r: p.r,
    status: p.status,
  }));

  // Fit-to-viewport. Scale both positions and radii uniformly so
  // the farthest engine's outer edge lands exactly on OUTER_R.
  // This is two-directional: compact layouts (a single engine,
  // tight clusters) scale up to fill the available space; dense
  // stacks that didn't fully settle inside OUTER_R scale down to
  // fit. Because positions and radii scale by the same factor,
  // relative engine sizes (area ∝ thrust) are preserved and no
  // new overlaps appear.
  //
  // The scale is clamped so no single engine exceeds
  // MAX_ENGINE_R_FRACTION × OUTER_R after scaling; without that
  // cap, sparse layouts (one bell, two hugging the axis) would
  // bloat the dominant engine to fill the entire viewport.
  let extent = 0;
  let maxR = 0;
  for (const p of points) {
    const d = Math.hypot(p.cx, p.cy) + p.r;
    if (d > extent) extent = d;
    if (p.r > maxR) maxR = p.r;
  }
  if (extent > 1e-6 && maxR > 1e-6) {
    const fitScale = OUTER_R / extent;
    const capScale = (OUTER_R * MAX_ENGINE_R_FRACTION) / maxR;
    const k = Math.min(fitScale, capScale);
    for (const p of points) {
      p.cx *= k;
      p.cy *= k;
      p.r *= k;
    }
  }

  return { points };
}
