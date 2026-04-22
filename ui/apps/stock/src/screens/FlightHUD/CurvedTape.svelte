<script lang="ts">
  import type { TapeScale } from './tape-scales';
  import {
    CURVED_TAPE_SIZE,
    CURVED_TICK_INNER_R,
    CURVED_TICK_MINOR_R,
    CURVED_TICK_MAJOR_R,
    CURVED_LABEL_R,
    CURVED_VISIBLE_HALF_ARC,
    PANEL_PATH_LEFT,
    PANEL_PATH_RIGHT,
  } from './tape-scales';

  let {
    side,
    value,
    modeLabel,
    scale,
    formatReadout,
  }: {
    side: 'left' | 'right';
    value: number;
    modeLabel: string;
    scale: TapeScale;
    formatReadout: (v: number) => { value: string; unit: string };
  } = $props();

  let isLeft = $derived(side === 'left');
  let baseAngleDeg = $derived(isLeft ? 180 : 0);
  let sign = $derived(isLeft ? -1 : 1);
  let angleDir = $derived(isLeft ? 1 : -1);
  // Readout anchors: number sits at the tab's outer-reading-order end
  // (far-from-tip on left, near-tip on right), unit is pushed to the
  // opposite end of the tab rect so it can't crowd a growing number.
  let numX = $derived(isLeft ? -207 : 152);
  let unitX = $derived(isLeft ? -152 : 207);
  let panelPath = $derived(isLeft ? PANEL_PATH_LEFT : PANEL_PATH_RIGHT);
  let clipId = $derived(`curved-tape-clip-${side}`);

  let clamped = $derived(Math.max(0, value));
  let visible = $derived(scale.visibleTicks(clamped, CURVED_VISIBLE_HALF_ARC));

  let formatted = $derived(formatReadout(clamped));
  let half = CURVED_TAPE_SIZE / 2;

  let tabPoints = $derived(
    `${sign * 210},-12 ${sign * 150},-12 ${sign * 133},0 ` +
    `${sign * 150},12 ${sign * 210},12`
  );
</script>

<svg
  class="curved-tape"
  viewBox="{-half} {-half} {CURVED_TAPE_SIZE} {CURVED_TAPE_SIZE}"
>
  <defs>
    <clipPath id={clipId}>
      <path d={panelPath} />
    </clipPath>
  </defs>

  <path class="curved-tape__panel" d={panelPath} />

  <g clip-path="url(#{clipId})">
    {#each visible as t (t.value)}
      {@const angleRad = ((baseAngleDeg + angleDir * t.deltaDeg) * Math.PI) / 180}
      {@const cos = Math.cos(angleRad)}
      {@const sin = Math.sin(angleRad)}
      {@const outerR = t.major ? CURVED_TICK_MAJOR_R : CURVED_TICK_MINOR_R}
      <line
        class="curved-tape__bar"
        class:curved-tape__bar--major={t.major}
        x1={cos * CURVED_TICK_INNER_R}
        y1={sin * CURVED_TICK_INNER_R}
        x2={cos * outerR}
        y2={sin * outerR}
      />
      {#if t.major}
        <text
          class="curved-tape__label"
          x={cos * CURVED_LABEL_R}
          y={sin * CURVED_LABEL_R}
          text-anchor="middle"
          dominant-baseline="central"
        >
          {t.label}
        </text>
      {/if}
    {/each}
  </g>

  <g class="curved-tape__mode">
    <rect
      x={isLeft ? -208 : 150}
      y={-30}
      width={58}
      height={14}
      rx={2}
    />
    <text
      x={sign * 179}
      y={-23}
      text-anchor="middle"
      dominant-baseline="central"
    >
      {modeLabel}
    </text>
  </g>

  <g class="curved-tape__cursor">
    <polygon class="curved-tape__readout-bg" points={tabPoints} />
    <text
      class="curved-tape__readout-num"
      x={numX}
      y={0}
      text-anchor="start"
      dominant-baseline="central"
    >
      {formatted.value}
    </text>
    <text
      class="curved-tape__readout-unit"
      x={unitX}
      y={0}
      text-anchor="end"
      dominant-baseline="central"
    >
      {formatted.unit}
    </text>
  </g>
</svg>
