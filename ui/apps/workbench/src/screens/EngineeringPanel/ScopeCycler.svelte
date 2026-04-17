<script lang="ts">
  import type { ScopeEntry } from './helpers';

  let { entries, activeIdx, onPrev, onNext }: {
    entries: ScopeEntry[];
    activeIdx: number;
    onPrev: () => void;
    onNext: () => void;
  } = $props();

  let current = $derived(entries[activeIdx]);
</script>

<div class="cycler" role="group" aria-label="Scope">
  <button type="button" onclick={onPrev} class="cycler__arrow" aria-label="Previous scope">◄</button>
  <div class="cycler__center">
    <div class="cycler__label-row">
      <span class="cycler__k">SCOPE</span>
      <span class="cycler__pos">
        {activeIdx + 1} / {entries.length}
      </span>
    </div>
    <div class="cycler__label">{current.label}</div>
    <div class="cycler__sub">{current.sub}</div>
    <div class="cycler__dots">
      {#each entries as _, i}
        <span class="cycler__dot" class:cycler__dot--active={i === activeIdx}></span>
      {/each}
    </div>
  </div>
  <button type="button" onclick={onNext} class="cycler__arrow" aria-label="Next scope">►</button>
</div>
