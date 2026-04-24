<script lang="ts">
  // Left-side editor parts catalog. Replaces stock's scrolling panel
  // with a category-tabbed + searchable list. Icons are deferred to a
  // later pass (stock renders them from Unity prefabs — cheapest path
  // is a server-side capture to sprite-sheet). For now each row is
  // text-only: title, cost/mass, manufacturer line.
  //
  // Click-to-pick is stubbed — the rune doesn't yet expose a
  // pickPart(name) op, so clicks currently log. Wiring the op is the
  // next follow-up once this panel lands.

  import { usePartCatalog, usePartCatalogOps } from '@dragonglass/telemetry/svelte';
  import type { PartCategory, PartCatalogEntry } from '@dragonglass/telemetry/core';

  // Order the category tabs appear in. Chosen to match stock KSP's
  // left-to-right ordering so muscle memory survives the swap.
  const TAB_ORDER: readonly PartCategory[] = [
    'Pods',
    'FuelTank',
    'Engine',
    'Control',
    'Structural',
    'Aero',
    'Coupling',
    'Payload',
    'Utility',
    'Science',
    'Communication',
    'Electrical',
    'Thermal',
    'Ground',
    'Robotics',
    'Cargo',
    'Propulsion',
  ];

  // Shorter labels for tabs. Stock's category names fit alongside
  // icons; a text-only chip row can't afford "Communication" at full
  // width in a narrow left pane.
  const TAB_LABEL: Record<PartCategory, string> = {
    Pods: 'Pods',
    FuelTank: 'Fuel',
    Engine: 'Engine',
    Control: 'Ctrl',
    Structural: 'Struct',
    Aero: 'Aero',
    Coupling: 'Couple',
    Payload: 'Payload',
    Utility: 'Util',
    Science: 'Sci',
    Communication: 'Comm',
    Electrical: 'Elec',
    Thermal: 'Therm',
    Ground: 'Ground',
    Robotics: 'Robot',
    Cargo: 'Cargo',
    Propulsion: 'Prop',
  };

  const catalog = usePartCatalog();
  const ops = usePartCatalogOps();

  let activeCategory = $state<PartCategory>('Pods');
  let search = $state('');

  // Categories with at least one part. Keeps tabs honest — if a
  // modded install has zero Robotics parts we don't render a dead
  // tab.
  const populated = $derived.by(() => {
    const set = new Set<PartCategory>();
    for (const e of catalog.entries) set.add(e.category);
    return TAB_ORDER.filter((c) => set.has(c));
  });

  // Filter + sort the visible parts. Search is a case-insensitive
  // substring match across title + tags — mirrors stock's search
  // behaviour. When the search box is empty the active tab drives
  // the result set; when it's non-empty the search overrides the
  // tab so the player can find a part without remembering which
  // category it lives under.
  const visible = $derived.by(() => {
    const q = search.trim().toLowerCase();
    const base = q.length === 0
      ? catalog.entries.filter((e) => e.category === activeCategory)
      : catalog.entries.filter((e) => {
          const hay = (e.title + ' ' + e.tags).toLowerCase();
          return hay.includes(q);
        });
    return [...base].sort((a, b) => a.title.localeCompare(b.title));
  });

  function onPick(entry: PartCatalogEntry) {
    // Send the pick over the wire. The server resolves the part by
    // name and hands it to stock's `EditorLogic.SpawnPart`, which
    // attaches the prefab to the cursor. From there stock's own
    // placement FSM takes over — the player moves the cursor into
    // the 3D viewport and clicks to drop onto an attach node.
    ops.pickPart(entry.name);
  }

  // Stop pointer-down from reaching the drag surface underneath
  // (editor ship viewport). Same pattern the PAW uses so clicks
  // don't double-count as "start rotating the ship".
  function halt(e: Event) {
    e.stopPropagation();
  }
</script>

<aside class="cat" onpointerdown={halt}>
  <header class="cat__head">
    <span class="cat__name">PARTS</span>
    <span class="cat__count">{catalog.entries.length}</span>
  </header>

  <input
    class="cat__search"
    type="search"
    placeholder="search parts"
    bind:value={search}
    onpointerdown={halt}
    aria-label="Search parts"
  />

  {#if search.trim().length === 0}
    <!-- Category strip. Horizontal scroll if they overflow — in a
         stock install they fit; modded installs may add categories. -->
    <nav class="cat__tabs" aria-label="Part categories">
      {#each populated as cat (cat)}
        <button
          type="button"
          class="cat__tab"
          class:cat__tab--active={cat === activeCategory}
          onclick={() => (activeCategory = cat)}
          onpointerdown={halt}
        >{TAB_LABEL[cat]}</button>
      {/each}
    </nav>
  {/if}

  <ul class="cat__list">
    {#each visible as entry (entry.name)}
      <li class="cat__entry">
        <button
          type="button"
          class="cat__entry-btn"
          onclick={() => onPick(entry)}
          onpointerdown={halt}
        >
          <span class="cat__title">{entry.title}</span>
          <span class="cat__meta">
            <em class="cat__meta-mass">{entry.mass.toFixed(2)}<small>t</small></em>
            <em class="cat__meta-cost">√{entry.cost.toFixed(0)}</em>
          </span>
        </button>
      </li>
    {:else}
      <li class="cat__empty">
        {search.trim().length > 0 ? 'no matches' : 'no parts in category'}
      </li>
    {/each}
  </ul>
</aside>

<style>
  .cat {
    position: fixed;
    top: 12px;
    left: 12px;
    bottom: 12px;
    width: 240px;
    display: flex;
    flex-direction: column;
    gap: 6px;
    padding: 8px 6px 8px 10px;
    color: var(--fg);
    font-family: var(--font-mono);
    font-size: 11px;
    background: var(--bg-panel-strong, rgba(10, 20, 16, 0.78));
    border: 1px solid var(--line, rgba(126, 245, 184, 0.22));
    box-shadow:
      inset 0 0 16px rgba(126, 245, 184, 0.04),
      0 0 24px rgba(0, 0, 0, 0.35);
    pointer-events: auto;
  }

  .cat__head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 2px 2px 6px;
    border-bottom: 1px solid var(--line, rgba(126, 245, 184, 0.18));
  }
  .cat__name {
    font-family: var(--font-display);
    letter-spacing: 0.28em;
    text-transform: uppercase;
    font-size: 10px;
    color: var(--fg-dim);
  }
  .cat__count {
    font-family: var(--font-mono);
    color: var(--accent);
    font-variant-numeric: tabular-nums;
    font-size: 10px;
    text-shadow: 0 0 4px var(--accent-glow);
  }

  .cat__search {
    width: 100%;
    padding: 4px 6px;
    font-family: var(--font-mono);
    font-size: 11px;
    color: var(--fg);
    background: rgba(0, 0, 0, 0.4);
    border: 1px solid var(--line);
    outline: none;
  }
  .cat__search::placeholder {
    color: var(--fg-mute);
    letter-spacing: 0.06em;
  }
  .cat__search:focus {
    border-color: var(--accent);
    box-shadow: 0 0 4px var(--accent-glow);
  }

  .cat__tabs {
    display: flex;
    flex-wrap: wrap;
    gap: 2px;
  }
  .cat__tab {
    padding: 2px 6px;
    font-family: var(--font-mono);
    font-size: 9px;
    letter-spacing: 0.1em;
    text-transform: uppercase;
    color: var(--fg-mute);
    background: transparent;
    border: 1px solid var(--line);
    cursor: pointer;
  }
  .cat__tab:hover {
    color: var(--fg-dim);
    border-color: var(--line-accent);
  }
  .cat__tab--active {
    color: var(--accent);
    border-color: var(--accent);
    background: rgba(126, 245, 184, 0.12);
    text-shadow: 0 0 3px var(--accent-glow);
  }

  .cat__list {
    list-style: none;
    margin: 0;
    padding: 0;
    flex: 1 1 auto;
    overflow-y: auto;
    display: flex;
    flex-direction: column;
    gap: 2px;
  }
  .cat__list::-webkit-scrollbar {
    width: 6px;
  }
  .cat__list::-webkit-scrollbar-thumb {
    background: rgba(126, 245, 184, 0.25);
    border-radius: 0;
  }

  .cat__entry {
    display: flex;
  }
  .cat__entry-btn {
    flex: 1 1 auto;
    display: flex;
    flex-direction: column;
    gap: 1px;
    padding: 4px 6px;
    text-align: left;
    background: transparent;
    border: 1px solid transparent;
    cursor: pointer;
    transition: background 140ms ease, border-color 140ms ease;
  }
  .cat__entry-btn:hover {
    background: rgba(126, 245, 184, 0.08);
    border-color: var(--line-accent);
  }

  .cat__title {
    color: var(--fg);
    font-family: var(--font-mono);
    font-size: 11px;
    line-height: 1.15;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .cat__meta {
    display: flex;
    gap: 8px;
    font-family: var(--font-display);
    font-variant-numeric: tabular-nums;
    font-size: 9px;
    color: var(--fg-mute);
  }
  .cat__meta em {
    font-style: normal;
  }
  .cat__meta-mass small {
    color: var(--fg-mute);
    font-size: 8px;
    margin-left: 1px;
  }
  .cat__meta-cost {
    color: var(--accent);
    text-shadow: 0 0 2px var(--accent-glow);
  }

  .cat__empty {
    list-style: none;
    padding: 8px 6px;
    color: var(--fg-mute);
    text-align: center;
    font-size: 10px;
    letter-spacing: 0.1em;
    text-transform: uppercase;
  }
</style>
