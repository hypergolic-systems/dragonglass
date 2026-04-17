<script lang="ts">
  import type { AssemblyModel } from '@dragonglass/telemetry/core';
  import type { Scope } from './power';
  import { categorizeAttitude, totalSasTorque } from './propulsion';
  import AccStat from './AccStat.svelte';
  import EmptyBody from './EmptyBody.svelte';
  import CategoryGroup from './CategoryGroup.svelte';

  let { assembly, scope }: { assembly: AssemblyModel; scope: Scope } = $props();

  let open = $state(true);
  let openCats = $state<Record<string, boolean>>({});

  let categories = $derived(categorizeAttitude(assembly, scope));
  let torque = $derived(totalSasTorque(assembly, scope));
  let isEmpty = $derived(categories.length === 0);
</script>

<section class="acc acc--power" class:acc--open={open}>
  <button type="button" class="acc__head" onclick={() => open = !open}>
    <span class="acc__chev">{open ? '▼' : '►'}</span>
    <span class="acc__title">ATTITUDE</span>
    <span class="acc__flex"></span>
    <span class="acc__inline-stats">
      {#if isEmpty}
        <span class="accstat accstat--dim">
          <span class="accstat__v">NO ATTITUDE SRC</span>
        </span>
      {:else}
        <AccStat label="SAS" value="{torque.toFixed(1)} kN·m" tone="good" />
      {/if}
    </span>
  </button>
  {#if open}
    <div class="acc__body">
      {#if isEmpty}
        <EmptyBody label="NO TORQUE SOURCES IN SCOPE" />
      {:else}
        {#each categories as c (c.id)}
          <CategoryGroup
            category={c}
            open={openCats[c.id] ?? true}
            onToggleOpen={() => openCats = { ...openCats, [c.id]: !(openCats[c.id] ?? true) }}
            powered={{}}
            onTogglePart={() => {}}
            onToggleCategory={() => {}}
          />
        {/each}
      {/if}
    </div>
  {/if}
</section>
