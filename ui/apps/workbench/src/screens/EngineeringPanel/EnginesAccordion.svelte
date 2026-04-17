<script lang="ts">
  import type { AssemblyModel } from '@dragonglass/telemetry/core';
  import type { Scope, TaggedPart, CategoryId } from './power';
  import { categorizeEngines, computePropulsionStats, formatBurnTime } from './propulsion';
  import AccStat from './AccStat.svelte';
  import EmptyBody from './EmptyBody.svelte';
  import CategoryGroup from './CategoryGroup.svelte';

  let { assembly, scope, enabled, onTogglePart, onToggleCategoryParts }: {
    assembly: AssemblyModel;
    scope: Scope;
    enabled: Record<string, boolean>;
    onTogglePart: (id: string) => void;
    onToggleCategoryParts: (items: TaggedPart[], id: CategoryId) => void;
  } = $props();

  let open = $state(true);
  let openCats = $state<Record<string, boolean>>({});

  let categories = $derived(categorizeEngines(assembly, scope));
  let stats = $derived(computePropulsionStats(assembly, scope, enabled));
  let isEmpty = $derived(categories.length === 0);

  function toggleCat(key: string) {
    openCats = { ...openCats, [key]: !openCats[key] };
  }
</script>

<section class="acc acc--power" class:acc--open={open}>
  <button type="button" class="acc__head" onclick={() => open = !open}>
    <span class="acc__chev">{open ? '▼' : '►'}</span>
    <span class="acc__title">PROPULSION</span>
    <span class="acc__flex"></span>
    <span class="acc__inline-stats">
      {#if isEmpty}
        <span class="accstat accstat--dim">
          <span class="accstat__v">NO ENGINES</span>
        </span>
      {:else if stats.hasThrust}
        <AccStat label="ΔV" value="{stats.deltaV.toFixed(0)} m/s" tone="good" />
        <AccStat label="TWR" value={stats.twrKerbin.toFixed(2)} tone="dim" />
        <AccStat label="BURN" value={formatBurnTime(stats.burnTimeSeconds)} tone="dim" />
      {:else}
        <AccStat label="ΔV" value="—" tone="dim" />
        <AccStat label="TWR" value="—" tone="dim" />
        <AccStat label="BURN" value="—" tone="dim" />
      {/if}
    </span>
  </button>
  {#if open}
    <div class="acc__body">
      {#if isEmpty}
        <EmptyBody label="NO ENGINES IN SCOPE" />
      {:else}
        {#each categories as c, i (c.label + ':' + i)}
          {@const key = c.label + ':' + i}
          <CategoryGroup
            category={c}
            open={openCats[key] ?? true}
            onToggleOpen={() => toggleCat(key)}
            powered={enabled}
            onTogglePart={onTogglePart}
            onToggleCategory={() => onToggleCategoryParts(c.items, c.id)}
          />
        {/each}
      {/if}
    </div>
  {/if}
</section>
