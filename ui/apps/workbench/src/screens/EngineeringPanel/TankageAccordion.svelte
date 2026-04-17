<script lang="ts">
  import type { AssemblyModel } from '@dragonglass/telemetry/core';
  import type { Scope } from './power';
  import { categorizeTankage, totalTankageMass } from './propulsion';
  import AccStat from './AccStat.svelte';
  import EmptyBody from './EmptyBody.svelte';
  import CategoryGroup from './CategoryGroup.svelte';

  let { assembly, scope }: { assembly: AssemblyModel; scope: Scope } = $props();

  let open = $state(true);
  let openCats = $state<Record<string, boolean>>({});

  let categories = $derived(categorizeTankage(assembly, scope));
  let total = $derived(totalTankageMass(assembly, scope));

  function toggleCat(key: string) {
    openCats = { ...openCats, [key]: !openCats[key] };
  }
</script>

<section class="acc acc--power" class:acc--open={open}>
  <button type="button" class="acc__head" onclick={() => open = !open}>
    <span class="acc__chev">{open ? '▼' : '►'}</span>
    <span class="acc__title">TANKAGE</span>
    <span class="acc__flex"></span>
    <span class="acc__inline-stats">
      <AccStat label="FUEL" value="{total.toFixed(2)} t" tone="good" />
      <AccStat label="RES" value={String(categories.length)} tone="dim" />
    </span>
  </button>
  {#if open}
    <div class="acc__body">
      {#if categories.length === 0}
        <EmptyBody label="NO PROPELLANT TANKS IN SCOPE" />
      {:else}
        {#each categories as c, i (c.label + ':' + i)}
          {@const key = c.label + ':' + i}
          <CategoryGroup
            category={c}
            open={openCats[key] ?? true}
            onToggleOpen={() => toggleCat(key)}
            powered={{}}
            onTogglePart={() => {}}
            onToggleCategory={() => {}}
          />
        {/each}
      {/if}
    </div>
  {/if}
</section>
