<script lang="ts">
  // Annular-wedge indicator that caps the top of the navball. Matches
  // the tape's full radial depth (133 → 212) so the three panels read
  // as one continuous ring; small angular margins keep the strokes
  // from colliding at the seam. The label follows the midpoint arc
  // via a `<textPath>` so characters wrap around the sphere like the
  // compass letters on the ball itself.
  //
  // `kind` picks a stock-KSP-like position (SAS top-right, RCS
  // top-left) and an active color (info-blue for SAS, warn-amber for
  // RCS). `onclick` opts in to interactivity — when present the
  // wedge renders a hit path with button semantics; without it the
  // component is a read-only indicator.

  import {
    CURVED_TAPE_SIZE,
    CURVED_PANEL_INNER_R,
    CURVED_PANEL_OUTER_R,
  } from './tape-scales';

  const {
    kind,
    active,
    onclick,
  }: {
    kind: 'sas' | 'rcs';
    active: boolean;
    onclick?: () => void;
  } = $props();

  const INNER_R = CURVED_PANEL_INNER_R;
  const OUTER_R = CURVED_PANEL_OUTER_R;
  // 16° half-arc at each of two wedges leaves ~6° of clearance to the
  // tape edges (at 228° / 312°) and ~8° of open sky between RCS and
  // SAS at the very top.
  const HALF_ARC_DEG = 16;
  const LABEL_R = (INNER_R + OUTER_R) / 2;
  const half = CURVED_TAPE_SIZE / 2;

  const CONFIG = {
    sas: { label: 'SAS', baseAngleDeg: 290 },
    rcs: { label: 'RCS', baseAngleDeg: 250 },
  } as const;

  const label = $derived(CONFIG[kind].label);
  const baseAngleDeg = $derived(CONFIG[kind].baseAngleDeg);
  const pathId = $derived(`nav-indicator-label-${kind}`);

  const toXY = (deg: number, r: number): [number, number] => {
    const rad = (deg * Math.PI) / 180;
    return [Math.cos(rad) * r, Math.sin(rad) * r];
  };

  function wedgePath(
    baseDeg: number,
    halfDeg: number,
    rIn: number,
    rOut: number,
  ): string {
    const [x1, y1] = toXY(baseDeg - halfDeg, rIn);
    const [x2, y2] = toXY(baseDeg + halfDeg, rIn);
    const [x3, y3] = toXY(baseDeg + halfDeg, rOut);
    const [x4, y4] = toXY(baseDeg - halfDeg, rOut);
    const p = (n: number) => n.toFixed(2);
    return (
      `M ${p(x1)} ${p(y1)}` +
      ` A ${rIn} ${rIn} 0 0 1 ${p(x2)} ${p(y2)}` +
      ` L ${p(x3)} ${p(y3)}` +
      ` A ${rOut} ${rOut} 0 0 0 ${p(x4)} ${p(y4)}` +
      ' Z'
    );
  }

  // Arc for the label to wrap around — midpoint radius, from lo to hi
  // angle. Drawn in increasing-angle direction so text reads along
  // the natural left-to-right sweep across the top of the sphere.
  function labelArc(baseDeg: number, halfDeg: number, r: number): string {
    const [x1, y1] = toXY(baseDeg - halfDeg, r);
    const [x2, y2] = toXY(baseDeg + halfDeg, r);
    const p = (n: number) => n.toFixed(2);
    return `M ${p(x1)} ${p(y1)} A ${r} ${r} 0 0 1 ${p(x2)} ${p(y2)}`;
  }

  const panelPath = $derived(
    wedgePath(baseAngleDeg, HALF_ARC_DEG, INNER_R, OUTER_R),
  );
  const textArc = $derived(labelArc(baseAngleDeg, HALF_ARC_DEG, LABEL_R));
</script>

<svg
  class="nav-indicator"
  class:nav-indicator--active={active}
  class:nav-indicator--sas={kind === 'sas'}
  class:nav-indicator--rcs={kind === 'rcs'}
  viewBox="{-half} {-half} {CURVED_TAPE_SIZE} {CURVED_TAPE_SIZE}"
  aria-hidden={onclick ? undefined : true}
>
  <defs>
    <path id={pathId} d={textArc} />
  </defs>

  <path class="nav-indicator__panel" d={panelPath} />

  <text class="nav-indicator__label" dominant-baseline="central">
    <textPath href="#{pathId}" startOffset="50%" text-anchor="middle">
      {label}
    </textPath>
  </text>

  {#if onclick}
    <path
      class="nav-indicator__hit"
      d={panelPath}
      role="button"
      tabindex="0"
      aria-label="{label} {active ? 'on' : 'off'}"
      aria-pressed={active}
      {onclick}
      onkeydown={(e) => {
        if (e.key === 'Enter' || e.key === ' ') {
          e.preventDefault();
          onclick();
        }
      }}
    />
  {/if}
</svg>
