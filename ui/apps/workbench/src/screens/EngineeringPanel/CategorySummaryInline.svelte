<script lang="ts">
  import { PROPELLANT_DENSITY, type ResourceId } from '@dragonglass/telemetry/core';
  import type { Category } from './power';
  import { fmtFlow, fmtStorage, pctLabel } from './helpers';

  let { category }: { category: Category } = $props();

  const resourceByLabel: Record<string, ResourceId> = {
    RP1: 'LF',
    LOX: 'Ox',
    HYDRAZINE: 'Mono',
  };

  let enginesSum = $derived.by(() => {
    if (category.id !== 'engines' && category.id !== 'rcs') return null;
    return category.items.reduce((a, i) => a + (i.part.engine?.thrust ?? 0), 0);
  });

  let propellantSum = $derived.by(() => {
    if (category.id !== 'propellant') return null;
    const onlyKey = resourceByLabel[category.label];
    let mass = 0;
    let current = 0;
    let capacity = 0;
    for (const { part } of category.items) {
      if (!part.tanks) continue;
      for (const key of Object.keys(part.tanks) as ResourceId[]) {
        if (onlyKey && key !== onlyKey) continue;
        const t = part.tanks[key];
        if (t) {
          mass += t.current * PROPELLANT_DENSITY[key];
          current += t.current;
          capacity += t.capacity;
        }
      }
    }
    return { mass, current, capacity };
  });

  let attitudeSum = $derived.by(() => {
    if (category.id !== 'attitude') return null;
    return category.items.reduce((a, i) => a + (i.part.sasTorque ?? 0), 0);
  });

  let vesselsSum = $derived.by(() => {
    if (category.id !== 'vessels') return null;
    const entries = category.vessels ?? [];
    if (entries.length === 0) return { kind: 'empty' as const };
    if (entries.length === 1) return { kind: 'single' as const, flow: entries[0].flow };
    const total = entries.reduce((a, e) => a + e.flow, 0);
    return { kind: 'multi' as const, flow: total };
  });
</script>

{#if enginesSum !== null}
  <span class="catg__sum catg__sum--good">
    <span class="catg__sum-num">{enginesSum.toFixed(1)}</span>
    <span class="catg__sum-unit">kN</span>
  </span>
{:else if propellantSum !== null}
  <span class="catg__sum catg__sum--info">
    <span class="catg__sum-num">{propellantSum.mass.toFixed(2)}</span>
    <span class="catg__sum-unit">
      t · {Math.round(propellantSum.current)}/{Math.round(propellantSum.capacity)} U
    </span>
  </span>
{:else if attitudeSum !== null}
  <span class="catg__sum catg__sum--good">
    <span class="catg__sum-num">{attitudeSum.toFixed(1)}</span>
    <span class="catg__sum-unit">kN·m</span>
  </span>
{:else if vesselsSum !== null}
  {#if vesselsSum.kind === 'empty'}
    <span class="catg__sum catg__sum--mute">—</span>
  {:else}
    <span class="catg__sum catg__sum--{vesselsSum.flow >= 0 ? 'good' : 'warn'}">
      <span class="catg__sum-num">{fmtFlow(vesselsSum.flow)}</span>
      <span class="catg__sum-unit">EC/s{vesselsSum.kind === 'multi' ? ' NET' : ''}</span>
    </span>
  {/if}
{:else if category.id === 'batteries' && category.summary.storage}
  <span class="catg__sum catg__sum--info">
    <span class="catg__sum-num">{fmtStorage(category.summary.storage)}</span>
    <span class="catg__sum-unit">EC · {pctLabel(category.summary.storage)}</span>
  </span>
{:else if category.summary.gen !== undefined}
  <span class="catg__sum catg__sum--good">
    <span class="catg__sum-num">{fmtFlow(category.summary.gen)}</span>
    <span class="catg__sum-unit">EC/s</span>
  </span>
{:else if category.summary.draw !== undefined}
  <span class="catg__sum catg__sum--dim">
    <span class="catg__sum-num">{fmtFlow(-category.summary.draw)}</span>
    <span class="catg__sum-unit">EC/s</span>
  </span>
{:else}
  <span class="catg__sum catg__sum--mute">—</span>
{/if}
