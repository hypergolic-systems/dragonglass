<script lang="ts">
  import type { PartModel, ResourceId } from '@dragonglass/telemetry/core';
  import type { CategoryId } from './power';
  import { detailRowsFor } from './helpers';
  import DetailRowView from './DetailRowView.svelte';

  let { part, categoryId, off, focusResource }: {
    part: PartModel;
    categoryId: CategoryId;
    off: boolean;
    focusResource?: ResourceId;
  } = $props();

  let rows = $derived(detailRowsFor(part, categoryId, off, focusResource));
</script>

<dl class="pdet">
  {#each rows as row (row.k)}
    <DetailRowView {row} />
  {/each}
</dl>
