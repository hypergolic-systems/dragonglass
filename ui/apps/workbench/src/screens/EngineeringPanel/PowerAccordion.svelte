<script lang="ts">
  import type { AssemblyModel } from '@dragonglass/telemetry/core';
  import type { Scope, TaggedPart, CategoryId } from './power';
  import { applyPowerState, fmtFlow, pctLabel, isToggleable } from './helpers';
  import { categorizePower, summarizeAssembly, summarizeVessel } from './power';
  import AccStat from './AccStat.svelte';
  import CategoryGroup from './CategoryGroup.svelte';

  let { assembly, scope }: { assembly: AssemblyModel; scope: Scope } = $props();

  let open = $state(true);
  let openCats = $state<Record<string, boolean>>({
    generation: true,
    batteries: true,
    command: true,
    lifesupport: true,
    science: true,
    comms: true,
    vessels: true,
  });

  let powered = $state<Record<string, boolean>>((() => {
    const init: Record<string, boolean> = {};
    for (const v of assembly.vessels) {
      for (const p of v.parts) {
        if (p.kind === 'solar' || p.kind === 'science' || p.kind === 'antenna' || p.kind === 'pod') {
          init[p.id] = true;
        }
      }
    }
    return init;
  })());

  let effectiveAssembly = $derived(applyPowerState(assembly, powered));

  let summary = $derived(
    scope.kind === 'assembly'
      ? summarizeAssembly(effectiveAssembly)
      : summarizeVessel(
          effectiveAssembly.vessels.find((v) => v.id === (scope as { kind: 'vessel'; id: string }).id)!,
        ),
  );

  let categories = $derived(categorizePower(effectiveAssembly, scope));

  function toggleCat(id: CategoryId) {
    openCats = { ...openCats, [id]: !openCats[id] };
  }

  function togglePart(id: string) {
    powered = { ...powered, [id]: !(powered[id] ?? true) };
  }

  function toggleCategoryParts(items: TaggedPart[], categoryId: CategoryId) {
    const toggleables = items.filter((i) => isToggleable(i.part.kind, categoryId));
    if (toggleables.length === 0) return;
    const allOn = toggleables.every((i) => powered[i.part.id] ?? true);
    const target = !allOn;
    const next = { ...powered };
    for (const i of toggleables) next[i.part.id] = target;
    powered = next;
  }
</script>

<section class="acc acc--power" class:acc--open={open}>
  <button type="button" class="acc__head" onclick={() => open = !open}>
    <span class="acc__chev">{open ? '▼' : '►'}</span>
    <span class="acc__title">POWER</span>
    <span class="acc__flex"></span>
    <span class="acc__inline-stats">
      <AccStat label="GEN" value={summary.gen > 0 ? fmtFlow(summary.gen) : '—'} tone="good" />
      <AccStat label="DRAW" value={fmtFlow(-summary.draw)} tone="dim" />
      <AccStat label="SoC" value={pctLabel(summary.storage)} tone="good" />
    </span>
  </button>
  {#if open}
    <div class="acc__body">
      {#each categories as c (c.id)}
        <CategoryGroup
          category={c}
          open={openCats[c.id] ?? true}
          onToggleOpen={() => toggleCat(c.id)}
          powered={powered}
          onTogglePart={togglePart}
          onToggleCategory={() => toggleCategoryParts(c.items, c.id)}
        />
      {/each}
    </div>
  {/if}
</section>
