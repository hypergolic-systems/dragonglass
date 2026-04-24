<script lang="ts">
  // Parts Requisition Terminal. Stock KSP's parts drawer is category-
  // first: you pick a tab, you see the parts in that tab. That forces
  // the player to know which category a part lives in — airbrakes
  // under Aero vs Utility is the classic example.
  //
  // This panel inverts the interaction: every part is visible by
  // default, search narrows, category chips filter on top. Rows carry
  // a tech-tier band on the left edge so late-game parts read as
  // distinct from starter tier without needing a tooltip. Hovering a
  // row exposes a "dispatch" arrow that reads as "send to cursor" —
  // what actually happens when you click: the server calls
  // EditorLogic.SpawnPart and stock's placement FSM takes it from
  // there.
  //
  // Keyboard: `/` focuses the search input (mirrors web-search muscle
  // memory); Escape clears search and unfocuses.

  import { onMount } from 'svelte';
  import { usePartCatalog, usePartCatalogOps } from '@dragonglass/telemetry/svelte';
  import type { PartCategory, PartCatalogEntry } from '@dragonglass/telemetry/core';

  // Stock chip order. Keeps chip rhythm consistent across installs
  // even when a modded pack adds new categories.
  const CHIP_ORDER: readonly PartCategory[] = [
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

  // Condensed labels so the chip row fits in a 240 px-wide panel. A
  // full "Communication" doesn't fit next to a sibling chip; stock
  // uses an icon instead. We lean on short labels since the chip
  // itself is tiny and icons would add capture complexity.
  const CHIP_LABEL: Record<PartCategory, string> = {
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

  // Tech-tier hint → hue on the left-edge band. Ordered from starter
  // (dim accent) through intermediate (info blue) to advanced (warn
  // amber) to late-game (alert red). The classifier is a lookup table
  // over well-known stock tech nodes; unknowns fall through to the
  // dim starter tint so the tier band doesn't overstate confidence.
  type Tier = 'basic' | 'mid' | 'adv' | 'late';
  const TIER_BAND: Record<Tier, string> = {
    basic: 'var(--accent-dim)',
    mid:   'var(--info)',
    adv:   'var(--warn)',
    late:  'var(--alert)',
  };
  const TECH_TIER: Record<string, Tier> = {
    start: 'basic', basicRocketry: 'basic', engineering101: 'basic', generalConstruction: 'basic',
    survivability: 'basic', basicScience: 'basic', flightControl: 'basic',
    generalRocketry: 'mid', advRocketry: 'mid', aerodynamicSystems: 'mid', stability: 'mid',
    electrics: 'mid', advFlightControl: 'mid', advConstruction: 'mid',
    heavyRocketry: 'adv', heavierRocketry: 'adv', advElectrics: 'adv', largeElectrics: 'adv',
    precisionPropulsion: 'adv', advAerodynamics: 'adv', fuelSystems: 'adv',
    largeControl: 'late', heavyAerodynamics: 'late', nuclearPropulsion: 'late',
    experimentalElectrics: 'late', experimentalMotors: 'late', ionPropulsion: 'late',
  };
  function tierOf(entry: PartCatalogEntry): Tier {
    if (!entry.techRequired) return 'basic';
    return TECH_TIER[entry.techRequired] ?? 'basic';
  }

  const catalog = usePartCatalog();
  const ops = usePartCatalogOps();

  // `null` = no category filter (all parts visible). A chip click
  // toggles: clicking the active chip again clears back to null.
  let activeCategory = $state<PartCategory | null>(null);
  let search = $state('');

  let searchEl = $state<HTMLInputElement | null>(null);

  onMount(() => {
    // `/` to focus search — web-search muscle memory. Only hijack
    // when nothing else has focus, otherwise typing `/` in any text
    // field would steal the character.
    const onKey = (e: KeyboardEvent) => {
      if (e.key === '/' && document.activeElement === document.body) {
        e.preventDefault();
        searchEl?.focus();
      }
      if (e.key === 'Escape' && document.activeElement === searchEl) {
        search = '';
        searchEl?.blur();
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  });

  // Per-category part counts. Computed once per catalog change so
  // the chip row shows "Pods · 12" without rescanning on every
  // render. Populated-only — empty categories drop out of the chip
  // row entirely rather than showing "Robotics · 0".
  const counts = $derived.by(() => {
    const map = new Map<PartCategory, number>();
    for (const e of catalog.entries) {
      map.set(e.category, (map.get(e.category) ?? 0) + 1);
    }
    return map;
  });

  const populated = $derived(
    CHIP_ORDER.filter((c) => (counts.get(c) ?? 0) > 0),
  );

  // Filter + sort. When search is non-empty it takes precedence over
  // the category chip — the player can find a part across categories
  // without knowing which bin it lives in.
  const visible = $derived.by(() => {
    const q = search.trim().toLowerCase();
    let base: readonly PartCatalogEntry[] = catalog.entries;
    if (q.length > 0) {
      base = base.filter((e) => {
        const hay = (e.title + ' ' + e.tags + ' ' + e.manufacturer).toLowerCase();
        return hay.includes(q);
      });
    } else if (activeCategory !== null) {
      base = base.filter((e) => e.category === activeCategory);
    }
    // Stable sort: title asc. Later: group by tech tier or sort by
    // recently-picked, but those need per-session persistence.
    return [...base].sort((a, b) => a.title.localeCompare(b.title));
  });

  function onPick(entry: PartCatalogEntry) {
    ops.pickPart(entry.name);
  }

  function toggleCategory(c: PartCategory) {
    activeCategory = activeCategory === c ? null : c;
  }

  // Stop pointer-down from reaching the drag surface underneath
  // (editor ship viewport). Same pattern the PAW uses.
  function halt(e: Event) { e.stopPropagation(); }

  const queryActive = $derived(search.trim().length > 0);
</script>

<aside class="req" onpointerdown={halt}>
  <!-- Station masthead. "REQUISITION" frames the panel as a parts-
       dispatch console rather than a neutral "list of parts" —
       wayfinding that tells the player what the click is doing. -->
  <header class="req__head">
    <span class="req__mark" aria-hidden="true"></span>
    <h2 class="req__title">REQUISITION</h2>
    <span class="req__count" aria-label="{catalog.entries.length} parts indexed">
      {catalog.entries.length}
    </span>
  </header>

  <!-- Search row. Lens glyph on the left to establish "look up"; the
       `/` keyboard hint on the right reinforces discoverability. -->
  <div class="req__search-wrap" class:req__search-wrap--live={queryActive}>
    <svg class="req__search-icon" viewBox="0 0 20 20" aria-hidden="true">
      <circle cx="8.5" cy="8.5" r="5" fill="none" stroke="currentColor" stroke-width="1.4" />
      <line x1="12.5" y1="12.5" x2="17" y2="17" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" />
    </svg>
    <input
      bind:this={searchEl}
      class="req__search"
      type="search"
      placeholder="query index"
      bind:value={search}
      onpointerdown={halt}
      aria-label="Search parts"
    />
    {#if !queryActive}
      <kbd class="req__search-kbd" aria-hidden="true">/</kbd>
    {:else}
      <button
        type="button"
        class="req__search-clear"
        onclick={() => { search = ''; searchEl?.focus(); }}
        onpointerdown={halt}
        aria-label="Clear search"
      >×</button>
    {/if}
  </div>

  <!-- Filter chips. Click to filter, click the same chip again to
       clear (activeCategory flips back to null). When search is live
       the chips hide — the query drives results and a stale chip
       highlight would imply a filter that isn't active. -->
  {#if !queryActive}
    <nav class="req__chips" aria-label="Category filters">
      <button
        type="button"
        class="req__chip"
        class:req__chip--all={activeCategory === null}
        onclick={() => (activeCategory = null)}
        onpointerdown={halt}
      >
        <span>All</span>
        <em>{catalog.entries.length}</em>
      </button>
      {#each populated as cat (cat)}
        <button
          type="button"
          class="req__chip"
          class:req__chip--active={cat === activeCategory}
          onclick={() => toggleCategory(cat)}
          onpointerdown={halt}
        >
          <span>{CHIP_LABEL[cat]}</span>
          <em>{counts.get(cat) ?? 0}</em>
        </button>
      {/each}
    </nav>
  {/if}

  <!-- Result strip header. The lit phosphor segment + the word
       DISPATCH sells the idea that each row is something you can
       hand off to the cursor. Result count flashes in on each
       query/filter change so the player notices the list narrowed. -->
  <div class="req__strip" aria-hidden="true">
    <span class="req__strip-mark"></span>
    <span class="req__strip-label">Dispatch</span>
    <span class="req__strip-count">{visible.length}</span>
  </div>

  <ul class="req__list">
    {#each visible as entry (entry.name)}
      {@const tier = tierOf(entry)}
      <li class="req__row">
        <button
          type="button"
          class="req__row-btn"
          onclick={() => onPick(entry)}
          onpointerdown={halt}
          title={entry.manufacturer}
          style:--tier-band={TIER_BAND[tier]}
        >
          <span class="req__row-tier" aria-hidden="true"></span>
          {#if entry.iconBase64}
            <img
              class="req__row-icon"
              src={`data:image/png;base64,${entry.iconBase64}`}
              alt=""
              aria-hidden="true"
              draggable="false"
            />
          {:else}
            <span class="req__row-icon req__row-icon--empty" aria-hidden="true"></span>
          {/if}
          <span class="req__row-text">
            <span class="req__row-title">{entry.title}</span>
            <span class="req__row-meta">
              <span class="req__meta-mass">{entry.mass.toFixed(2)}<em>t</em></span>
              <span class="req__meta-sep" aria-hidden="true">·</span>
              <span class="req__meta-cost">√{entry.cost.toFixed(0)}</span>
              {#if entry.manufacturer}
                <span class="req__meta-sep" aria-hidden="true">·</span>
                <span class="req__meta-mfr">{entry.manufacturer}</span>
              {/if}
            </span>
          </span>
          <!-- Dispatch arrow. CSS-only — hidden by default, slides in
               on hover/focus. Reads as "send to cursor". -->
          <span class="req__row-dispatch" aria-hidden="true">→</span>
        </button>
      </li>
    {:else}
      <li class="req__empty">
        <span class="req__empty-mark" aria-hidden="true">—</span>
        {queryActive ? 'no matches' : 'category empty'}
      </li>
    {/each}
  </ul>
</aside>

<style>
  .req {
    position: fixed;
    top: 12px;
    left: 12px;
    bottom: 12px;
    width: 264px;
    display: flex;
    flex-direction: column;
    gap: 8px;
    padding: 10px 8px 10px 12px;
    color: var(--fg);
    font-family: var(--font-mono);
    font-size: 11px;
    background: var(--bg-panel-strong);
    border: 1px solid var(--line-accent);
    box-shadow:
      inset 0 0 0 1px rgba(126, 245, 184, 0.05),
      inset 0 0 24px rgba(126, 245, 184, 0.025);
    pointer-events: auto;
  }
  /* CRT scan texture — same pattern as PartActionWindow so the panels
     read as siblings on the same CRT tube, not as independent
     windows. */
  .req::after {
    content: '';
    position: absolute;
    inset: 0;
    pointer-events: none;
    background:
      repeating-linear-gradient(
        to bottom,
        rgba(126, 245, 184, 0.02) 0,
        rgba(126, 245, 184, 0.02) 1px,
        transparent 1px,
        transparent 3px
      );
    mix-blend-mode: screen;
    z-index: 0;
  }
  .req > * {
    position: relative;
    z-index: 1;
  }

  /* ============================================================
     Masthead
     ============================================================ */
  .req__head {
    display: flex;
    align-items: center;
    gap: 8px;
    padding-bottom: 6px;
    border-bottom: 1px solid var(--line);
    position: relative;
  }
  /* Glowing station mark — a single phosphor square that reads as a
     status LED. Pulses on the ambient breath so the terminal feels
     live even when nothing is moving. */
  .req__mark {
    width: 6px;
    height: 6px;
    background: var(--accent);
    box-shadow: 0 0 5px var(--accent-glow);
    animation: req-mark-breath 2.6s ease-in-out infinite;
  }
  @keyframes req-mark-breath {
    0%, 100% { opacity: 0.5; }
    50% { opacity: 1; box-shadow: 0 0 8px var(--accent-glow); }
  }

  .req__title {
    flex: 1 1 auto;
    margin: 0;
    font-family: var(--font-display);
    font-size: 15px;
    font-weight: normal;
    letter-spacing: 0.24em;
    text-transform: uppercase;
    color: var(--accent);
    text-shadow: 0 0 6px var(--accent-glow), 0 0 14px rgba(126, 245, 184, 0.18);
  }
  .req__count {
    font-family: var(--font-display);
    font-size: 15px;
    color: var(--fg-dim);
    font-variant-numeric: tabular-nums;
    letter-spacing: 0.08em;
  }
  /* Short accent segment + hairline station-ident strip under the
     header, matching the PAW title divider pattern. */
  .req__head::after {
    content: '';
    position: absolute;
    left: 0;
    right: 0;
    bottom: -1px;
    height: 1px;
    background: linear-gradient(
      to right,
      var(--accent) 0,
      var(--accent) 28px,
      transparent 28px,
      transparent 32px,
      rgba(126, 245, 184, 0.2) 32px,
      rgba(126, 245, 184, 0.2) 100%
    );
    box-shadow: 0 0 3px var(--accent-glow);
  }

  /* ============================================================
     Search
     ============================================================ */
  .req__search-wrap {
    position: relative;
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 4px 6px 4px 8px;
    background: rgba(0, 0, 0, 0.4);
    border: 1px solid var(--line);
    transition: border-color 160ms ease, box-shadow 160ms ease;
  }
  .req__search-wrap:focus-within {
    border-color: var(--accent);
    box-shadow: 0 0 0 1px var(--accent-glow), inset 0 0 10px rgba(126, 245, 184, 0.06);
  }
  .req__search-wrap--live {
    border-color: var(--accent);
  }
  .req__search-icon {
    flex: 0 0 auto;
    width: 12px;
    height: 12px;
    color: var(--fg-mute);
  }
  .req__search-wrap:focus-within .req__search-icon,
  .req__search-wrap--live .req__search-icon {
    color: var(--accent);
    filter: drop-shadow(0 0 3px var(--accent-glow));
  }
  .req__search {
    flex: 1 1 auto;
    min-width: 0;
    font-family: var(--font-mono);
    font-size: 11px;
    color: var(--fg);
    background: transparent;
    border: none;
    outline: none;
    padding: 2px 0;
    letter-spacing: 0.04em;
  }
  .req__search::placeholder {
    color: var(--fg-mute);
    letter-spacing: 0.08em;
    text-transform: lowercase;
  }
  .req__search::-webkit-search-cancel-button {
    display: none;
  }

  .req__search-kbd {
    flex: 0 0 auto;
    font-family: var(--font-mono);
    font-size: 9px;
    color: var(--fg-mute);
    background: rgba(126, 245, 184, 0.05);
    border: 1px solid var(--line);
    padding: 1px 4px;
    letter-spacing: 0.1em;
  }
  .req__search-clear {
    flex: 0 0 auto;
    width: 16px;
    height: 16px;
    padding: 0;
    font-family: var(--font-mono);
    font-size: 14px;
    line-height: 1;
    color: var(--fg-mute);
    background: transparent;
    border: none;
    cursor: pointer;
    transition: color 140ms ease;
  }
  .req__search-clear:hover {
    color: var(--alert);
  }

  /* ============================================================
     Chips
     ============================================================ */
  .req__chips {
    display: flex;
    flex-wrap: wrap;
    gap: 3px;
  }
  .req__chip {
    display: flex;
    align-items: center;
    gap: 4px;
    padding: 2px 6px;
    font-family: var(--font-mono);
    font-size: 9px;
    letter-spacing: 0.1em;
    text-transform: uppercase;
    color: var(--fg-mute);
    background: transparent;
    border: 1px solid var(--line);
    cursor: pointer;
    transition:
      color 120ms ease,
      border-color 120ms ease,
      background 120ms ease;
  }
  .req__chip em {
    font-family: var(--font-display);
    font-style: normal;
    font-variant-numeric: tabular-nums;
    font-size: 9px;
    color: var(--fg-mute);
    letter-spacing: 0.04em;
  }
  .req__chip:hover {
    color: var(--fg-dim);
    border-color: var(--line-bright);
  }
  .req__chip:hover em {
    color: var(--fg-dim);
  }
  .req__chip--active,
  .req__chip--all {
    color: var(--accent);
    border-color: var(--accent);
    background: rgba(126, 245, 184, 0.1);
    text-shadow: 0 0 3px var(--accent-glow);
  }
  .req__chip--active em,
  .req__chip--all em {
    color: var(--accent-soft);
  }

  /* ============================================================
     Result strip — dispatch header
     ============================================================ */
  .req__strip {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 4px 4px 4px 0;
    font-family: var(--font-mono);
    font-size: 8px;
    letter-spacing: 0.22em;
    text-transform: uppercase;
    color: var(--fg-dim);
    border-bottom: 1px dashed var(--line);
  }
  .req__strip-mark {
    flex: 0 0 auto;
    width: 14px;
    height: 2px;
    background: var(--accent);
    box-shadow: 0 0 4px var(--accent-glow);
  }
  .req__strip-label {
    flex: 1 1 auto;
  }
  .req__strip-count {
    flex: 0 0 auto;
    font-family: var(--font-display);
    font-size: 10px;
    color: var(--accent);
    font-variant-numeric: tabular-nums;
    text-shadow: 0 0 3px var(--accent-glow);
    letter-spacing: 0.06em;
  }

  /* ============================================================
     Part rows
     ============================================================ */
  .req__list {
    list-style: none;
    margin: 0;
    padding: 0;
    flex: 1 1 auto;
    overflow-y: auto;
    /* A little interior padding so the focus ring on the first /
       last row doesn't clip against the scroll edge. */
    padding: 2px 0;
    display: flex;
    flex-direction: column;
    gap: 1px;
  }
  .req__list::-webkit-scrollbar {
    width: 6px;
  }
  .req__list::-webkit-scrollbar-track {
    background: transparent;
  }
  .req__list::-webkit-scrollbar-thumb {
    background: rgba(126, 245, 184, 0.18);
    border-radius: 0;
  }
  .req__list::-webkit-scrollbar-thumb:hover {
    background: rgba(126, 245, 184, 0.35);
  }

  .req__row {
    display: flex;
  }
  .req__row-btn {
    flex: 1 1 auto;
    position: relative;
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 4px 6px 4px 10px;
    text-align: left;
    background: transparent;
    border: 1px solid transparent;
    cursor: pointer;
    transition:
      background 140ms ease,
      border-color 140ms ease;
    overflow: hidden;
  }
  /* Tech-tier band pinned to the left edge. The colour comes from
     --tier-band set inline per row; a 2 px strip whose top/bottom
     taper suggests a rivet or standoff. */
  .req__row-tier {
    position: absolute;
    left: 0;
    top: 4px;
    bottom: 4px;
    width: 2px;
    background: var(--tier-band);
    box-shadow: 0 0 3px var(--tier-band);
    opacity: 0.7;
  }
  .req__row-btn:hover,
  .req__row-btn:focus-visible {
    background: rgba(126, 245, 184, 0.08);
    border-color: var(--line-accent);
    outline: none;
  }
  .req__row-btn:hover .req__row-tier,
  .req__row-btn:focus-visible .req__row-tier {
    opacity: 1;
  }
  .req__row-btn:hover .req__row-icon,
  .req__row-btn:focus-visible .req__row-icon {
    filter: drop-shadow(0 0 5px rgba(126, 245, 184, 0.45));
  }

  .req__row-icon {
    flex: 0 0 auto;
    width: 34px;
    height: 34px;
    object-fit: contain;
    transition: filter 160ms ease;
  }
  .req__row-icon--empty {
    display: block;
    border: 1px dashed var(--line);
    opacity: 0.3;
  }

  .req__row-text {
    flex: 1 1 auto;
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 1px;
  }
  .req__row-title {
    color: var(--fg);
    font-family: var(--font-mono);
    font-size: 11px;
    line-height: 1.2;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    letter-spacing: 0.02em;
  }
  .req__row-btn:hover .req__row-title,
  .req__row-btn:focus-visible .req__row-title {
    color: var(--accent-soft);
    text-shadow: 0 0 3px var(--accent-glow);
  }
  .req__row-meta {
    display: flex;
    align-items: baseline;
    gap: 4px;
    font-family: var(--font-display);
    font-variant-numeric: tabular-nums;
    font-size: 9px;
    color: var(--fg-mute);
    letter-spacing: 0.04em;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .req__meta-mass {
    color: var(--fg-dim);
  }
  .req__meta-mass em {
    font-style: normal;
    font-size: 8px;
    color: var(--fg-mute);
    margin-left: 1px;
  }
  .req__meta-cost {
    color: var(--accent);
    text-shadow: 0 0 2px var(--accent-glow);
  }
  .req__meta-mfr {
    color: var(--fg-mute);
    font-family: var(--font-mono);
    font-size: 9px;
    flex: 1 1 auto;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .req__meta-sep {
    color: var(--fg-mute);
    opacity: 0.5;
  }

  /* Dispatch arrow — tucked off to the right, slides in on hover.
     Reinforces that the click doesn't "select" the row, it dispatches
     the part to the cursor. */
  .req__row-dispatch {
    flex: 0 0 auto;
    font-family: var(--font-display);
    font-size: 14px;
    color: var(--accent);
    text-shadow: 0 0 4px var(--accent-glow);
    transform: translateX(6px);
    opacity: 0;
    transition: transform 160ms cubic-bezier(0.2, 0.8, 0.25, 1), opacity 160ms ease;
  }
  .req__row-btn:hover .req__row-dispatch,
  .req__row-btn:focus-visible .req__row-dispatch {
    transform: translateX(0);
    opacity: 1;
  }

  .req__empty {
    list-style: none;
    padding: 18px 6px;
    color: var(--fg-mute);
    text-align: center;
    font-size: 10px;
    letter-spacing: 0.2em;
    text-transform: uppercase;
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 4px;
  }
  .req__empty-mark {
    font-family: var(--font-display);
    color: var(--fg-mute);
    opacity: 0.5;
    font-size: 14px;
  }
</style>
