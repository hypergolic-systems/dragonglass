<script lang="ts">
  import type { AssemblyModel } from '@dragonglass/telemetry/core';
  import type { Scope, TaggedPart, CategoryId } from './power';
  import { categorizeRcs, totalRcsThrust } from './propulsion';
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

  let categories = $derived(categorizeRcs(assembly, scope));
  let thrust = $derived(totalRcsThrust(assembly, scope));
  let isEmpty = $derived(categories.length === 0);
</script>

<section class="acc acc--power" class:acc--open={open}>
  <button type="button" class="acc__head" onclick={() => open = !open}>
    <span class="acc__chev">{open ? '▼' : '►'}</span>
    <span class="acc__title">REACTION CONTROL</span>
    <span class="acc__flex"></span>
    <span class="acc__inline-stats">
      {#if isEmpty}
        <span class="accstat accstat--dim">
          <span class="accstat__v">NO RCS</span>
        </span>
      {:else}
        <AccStat label="THRUST" value="{thrust.toFixed(1)} kN" tone="good" />
      {/if}
    </span>
  </button>
  {#if open}
    <div class="acc__body">
      {#if isEmpty}
        <EmptyBody label="NO RCS BLOCKS IN SCOPE" />
      {:else}
        {#each categories as c (c.id)}
          <CategoryGroup
            category={c}
            open={openCats[c.id] ?? true}
            onToggleOpen={() => openCats = { ...openCats, [c.id]: !(openCats[c.id] ?? true) }}
            powered={enabled}
            onTogglePart={onTogglePart}
            onToggleCategory={() => onToggleCategoryParts(c.items, c.id)}
          />
        {/each}
      {/if}
    </div>
  {/if}
</section>
