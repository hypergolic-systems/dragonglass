<script lang="ts">
  import type { PartModel, ResourceId } from '@dragonglass/telemetry/core';
  import type { CategoryId } from './power';
  import { fmtFlow, fmtStorage, pctVal } from './helpers';

  let { part, categoryId, off, focusResource }: {
    part: PartModel;
    categoryId: CategoryId;
    off: boolean;
    focusResource?: ResourceId;
  } = $props();

  let tank = $derived.by(() => {
    if (categoryId !== 'propellant' || !part.tanks) return undefined;
    const key = focusResource ?? (Object.keys(part.tanks)[0] as ResourceId | undefined);
    return key ? { key, t: part.tanks[key] } : undefined;
  });
</script>

{#if off}
  <span class="pcard__stat pcard__stat--mute">
    <span class="pcard__num pcard__num--mute">OFF</span>
  </span>
{:else if categoryId === 'batteries' && part.ecStorage}
  <span class="pcard__stat pcard__stat--info">
    <span class="pcard__bar">
      <span
        class="pcard__bar-fill"
        style="width: {(pctVal(part.ecStorage) * 100).toFixed(1)}%"
      ></span>
    </span>
    <span class="pcard__num">{fmtStorage(part.ecStorage)}</span>
  </span>
{:else if (categoryId === 'engines' || categoryId === 'rcs') && part.engine}
  <span class="pcard__stat pcard__stat--good">
    <span class="pcard__num">{part.engine.thrust.toFixed(1)}</span>
    <span class="pcard__unit">kN</span>
  </span>
{:else if categoryId === 'attitude' && part.sasTorque}
  <span class="pcard__stat pcard__stat--good">
    <span class="pcard__num">{part.sasTorque.toFixed(1)}</span>
    <span class="pcard__unit">kN·m</span>
  </span>
{:else if categoryId === 'propellant' && tank?.t}
  <span class="pcard__stat pcard__stat--info">
    <span class="pcard__bar">
      <span
        class="pcard__bar-fill"
        style="width: {(pctVal(tank.t) * 100).toFixed(1)}%"
      ></span>
    </span>
    <span class="pcard__num">{fmtStorage(tank.t)}</span>
  </span>
{:else if part.ecFlow && part.ecFlow > 0}
  <span class="pcard__stat pcard__stat--good">
    <span class="pcard__num">{fmtFlow(part.ecFlow)}</span>
    <span class="pcard__unit">EC/s</span>
  </span>
{:else if part.ecFlow && part.ecFlow < 0}
  <span class="pcard__stat pcard__stat--dim">
    <span class="pcard__num">{fmtFlow(part.ecFlow)}</span>
    <span class="pcard__unit">EC/s</span>
  </span>
{:else}
  <span class="pcard__stat pcard__stat--mute">
    <span class="pcard__num pcard__num--mute">{part.status ?? 'idle'}</span>
  </span>
{/if}
