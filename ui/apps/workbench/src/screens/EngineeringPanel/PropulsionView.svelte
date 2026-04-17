<script lang="ts">
  import type { AssemblyModel } from '@dragonglass/telemetry/core';
  import type { Scope, TaggedPart, CategoryId } from './power';
  import { isToggleable } from './helpers';
  import TankageAccordion from './TankageAccordion.svelte';
  import EnginesAccordion from './EnginesAccordion.svelte';
  import RcsAccordion from './RcsAccordion.svelte';
  import AttitudeAccordion from './AttitudeAccordion.svelte';

  let { assembly, scope }: { assembly: AssemblyModel; scope: Scope } = $props();

  let enabled = $state<Record<string, boolean>>((() => {
    const init: Record<string, boolean> = {};
    for (const v of assembly.vessels) {
      for (const p of v.parts) {
        if (p.kind === 'engine' || p.kind === 'rcs') init[p.id] = true;
      }
    }
    return init;
  })());

  function togglePart(id: string) {
    enabled = { ...enabled, [id]: !(enabled[id] ?? true) };
  }

  function toggleCategoryParts(items: TaggedPart[], categoryId: CategoryId) {
    const toggleables = items.filter((i) => isToggleable(i.part.kind, categoryId));
    if (toggleables.length === 0) return;
    const allOn = toggleables.every((i) => enabled[i.part.id] ?? true);
    const target = !allOn;
    const next = { ...enabled };
    for (const i of toggleables) next[i.part.id] = target;
    enabled = next;
  }
</script>

<div class="sys">
  <TankageAccordion {assembly} {scope} />
  <EnginesAccordion
    {assembly}
    {scope}
    {enabled}
    onTogglePart={togglePart}
    onToggleCategoryParts={toggleCategoryParts}
  />
  <RcsAccordion
    {assembly}
    {scope}
    {enabled}
    onTogglePart={togglePart}
    onToggleCategoryParts={toggleCategoryParts}
  />
  <AttitudeAccordion {assembly} {scope} />
</div>
