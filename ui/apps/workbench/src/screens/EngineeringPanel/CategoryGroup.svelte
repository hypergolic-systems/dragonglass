<script lang="ts">
  import type { Category } from './power';
  import { isToggleable, categoryBulkState, RESOURCE_BY_LABEL } from './helpers';
  import PowerToggle from './PowerToggle.svelte';
  import CategorySummaryInline from './CategorySummaryInline.svelte';
  import PartCard from './PartCard.svelte';
  import VesselEntryCard from './VesselEntryCard.svelte';

  let { category, open, onToggleOpen, powered, onTogglePart, onToggleCategory }: {
    category: Category;
    open: boolean;
    onToggleOpen: () => void;
    powered: Record<string, boolean>;
    onTogglePart: (id: string) => void;
    onToggleCategory: () => void;
  } = $props();

  let isVessels = $derived(category.id === 'vessels');
  let isEmpty = $derived(
    isVessels
      ? (category.vessels?.length ?? 0) === 0
      : category.items.length === 0
  );
  let bulkState = $derived(
    isVessels ? null : categoryBulkState(category.items, powered, category.id)
  );
  let focusResource = $derived(
    category.id === 'propellant' ? RESOURCE_BY_LABEL[category.label] : undefined
  );
</script>

<section
  class="catg catg--{category.id}"
  class:catg--open={open}
  class:catg--empty={isEmpty}
>
  <button
    type="button"
    class="catg__head"
    onclick={onToggleOpen}
    disabled={isEmpty}
  >
    <span class="catg__chev">{isEmpty ? '·' : open ? '▾' : '▸'}</span>
    <span class="catg__label">{category.label}</span>
    <span class="catg__count">
      {isVessels ? (category.vessels?.length ?? 0) : category.items.length}
    </span>
    <span class="catg__flex"></span>
    {#if bulkState !== null}
      <PowerToggle state={bulkState} onToggle={onToggleCategory} title="Toggle all" />
    {/if}
    {#if isEmpty}
      <span class="catg__sum catg__sum--mute">—</span>
    {:else}
      <CategorySummaryInline {category} />
    {/if}
  </button>
  {#if open && !isEmpty}
    <div class="catg__body">
      {#if isVessels}
        {#each category.vessels ?? [] as entry (entry.vessel.id + ':' + entry.kind)}
          <VesselEntryCard {entry} />
        {/each}
      {:else}
        {#each category.items as { part } (part.id + (focusResource ? ':' + focusResource : ''))}
          {@const toggleable = isToggleable(part.kind, category.id)}
          <PartCard
            {part}
            categoryId={category.id}
            {toggleable}
            powered={powered[part.id] ?? true}
            onToggle={() => onTogglePart(part.id)}
            {focusResource}
          />
        {/each}
      {/if}
    </div>
  {/if}
</section>
