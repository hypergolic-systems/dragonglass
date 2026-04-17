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
  let textAnchor = $derived(isLeft ? 'end' : 'start');
  let panelPath = $derived(isLeft ? PANEL_PATH_LEFT : PANEL_PATH_RIGHT);
  let clipId = $derived(`curved-tape-clip-${side}`);

  let clamped = $derived(Math.max(0, Math.min(scale.max, value)));
  let cursorArc = $derived(scale.posDeg(clamped));

  let visible = $derived.by(() => {
    const result: {
      value: number;
      major: boolean;
      deltaDeg: number;
      label: string;
    }[] = [];
    for (const grid of scale.tickGrids) {
      for (let v = grid.minV; v < grid.maxV; v += grid.minorStep) {
        const deltaDeg = scale.posDeg(v) - cursorArc;
        if (Math.abs(deltaDeg) > CURVED_VISIBLE_HALF_ARC) continue;
        result.push({
          value: v,
          major: v % grid.majorStep === 0,
          deltaDeg,
          label: grid.formatLabel(v),
        });
      }
    }
    return result;
  });

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
      {@const edge = Math.max(0, 1 - Math.abs(t.deltaDeg) / CURVED_VISIBLE_HALF_ARC)}
      {@const opacity = 0.25 + 0.75 * Math.pow(edge, 0.5)}
      <g {opacity}>
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
            text-anchor={textAnchor}
            dominant-baseline="central"
          >
            {t.label}
          </text>
        {/if}
      </g>
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
      x={sign * 155}
      y={1}
      text-anchor={textAnchor}
      dominant-baseline="central"
    >
      <tspan class="curved-tape__readout-num">
        {formatted.value}
      </tspan>
      <tspan class="curved-tape__readout-unit" dx="3">
        {formatted.unit}
      </tspan>
    </text>
  </g>
</svg>
