<script lang="ts">
  import type { PartModel, ResourceId } from '@dragonglass/telemetry/core';
  import type { CategoryId } from './power';
  import { PART_ICON } from './helpers';
  import PartHeadStats from './PartHeadStats.svelte';
  import PartDetail from './PartDetail.svelte';
  import PowerToggle from './PowerToggle.svelte';

  let { part, categoryId, toggleable, powered, onToggle, focusResource }: {
    part: PartModel;
    categoryId: CategoryId;
    toggleable: boolean;
    powered: boolean;
    onToggle: () => void;
    focusResource?: ResourceId;
  } = $props();

  let open = $state(false);
  let off = $derived(toggleable && !powered);
</script>

<article
  class="pcard pcard--{categoryId}"
  class:pcard--open={open}
  class:pcard--off={off}
>
  <button type="button" class="pcard__head" onclick={() => open = !open}>
    <span class="pcard__chev">{open ? '▾' : '▸'}</span>
    <span class="pcard__icon">{PART_ICON[part.kind]}</span>
    <span class="pcard__name">{part.name}</span>
    <span class="pcard__flex"></span>
    {#if toggleable}
      <PowerToggle state={powered ? 'on' : 'off'} {onToggle} />
    {/if}
    <PartHeadStats {part} {categoryId} {off} {focusResource} />
  </button>
  {#if open}
    <div class="pcard__body">
      <PartDetail {part} {categoryId} {off} {focusResource} />
    </div>
  {/if}
</article>
