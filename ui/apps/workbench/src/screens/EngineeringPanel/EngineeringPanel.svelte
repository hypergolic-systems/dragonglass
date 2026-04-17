<script lang="ts">
  import type { Scope } from './power';
  import type { SubsystemId } from './helpers';
  import { useAssembly } from '@dragonglass/telemetry/svelte';
  import { SUBSYSTEMS, SUBSYSTEM_LABEL, buildScopeList, scopeIndex } from './helpers';
  import './EngineeringPanel.css';
  import ScopeCycler from './ScopeCycler.svelte';
  import SystemsView from './SystemsView.svelte';
  import PropulsionView from './PropulsionView.svelte';

  const assemblyRef = useAssembly();

  let scope = $state<Scope>({ kind: 'assembly' });
  let activeSubsystem = $state<SubsystemId>('sys');

  let assembly = $derived(assemblyRef.current);
  let scopeList = $derived(assembly ? buildScopeList(assembly) : []);
  let idx = $derived(scopeIndex(scopeList, scope));

  function goTo(i: number) {
    if (scopeList.length === 0) return;
    const wrapped = ((i % scopeList.length) + scopeList.length) % scopeList.length;
    scope = scopeList[wrapped].scope;
  }

  $effect(() => {
    const currentIdx = idx;
    const onKey = (e: KeyboardEvent) => {
      const target = e.target as HTMLElement | null;
      if (target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA')) return;
      if (e.key === 'ArrowLeft') {
        goTo(currentIdx - 1);
        e.preventDefault();
      } else if (e.key === 'ArrowRight') {
        goTo(currentIdx + 1);
        e.preventDefault();
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  });
</script>

{#if assembly}
  <div class="eng-stage">
    <div class="eng-stage__sky" aria-hidden="true"></div>
    <div class="eng-stage__stars" aria-hidden="true"></div>
    <div class="eng-stage__scanline" aria-hidden="true"></div>
    <div class="eng-stage__grain" aria-hidden="true"></div>
    <div class="eng-stage__camlabel" aria-hidden="true">
      <span>VESSEL CAM · FWD</span>
      <span class="eng-stage__camlabel-sub">ORBIT · KERBIN 72 km</span>
    </div>
    <div class="eng-stage__reticle" aria-hidden="true">
      <div class="eng-stage__reticle-h"></div>
      <div class="eng-stage__reticle-v"></div>
    </div>

    <section class="ewin" aria-label="Engineering panel">
      <header class="ewin__head">
        <span class="ewin__head-mark">◇</span>
        <span class="ewin__head-title">ENGINEERING</span>
        <span class="ewin__head-sep"></span>
        <span class="ewin__head-sub">{assembly.name}</span>
        <span class="ewin__head-spacer"></span>
        <button type="button" class="ewin__head-btn" aria-label="Minimize" tabindex={-1}>_</button>
        <button type="button" class="ewin__head-btn" aria-label="Close" tabindex={-1}>×</button>
      </header>

      <ScopeCycler
        entries={scopeList}
        activeIdx={idx}
        onPrev={() => goTo(idx - 1)}
        onNext={() => goTo(idx + 1)}
      />

      <nav class="syschips" aria-label="Subsystems">
        {#each SUBSYSTEMS as s (s.id)}
          {@const active = s.id === activeSubsystem}
          <button
            type="button"
            disabled={!s.enabled}
            title={s.label}
            onclick={() => s.enabled && (activeSubsystem = s.id)}
            class="syschip"
            class:syschip--active={active}
            class:syschip--disabled={!s.enabled}
          >
            <span class="syschip__short">{s.short}</span>
          </button>
        {/each}
      </nav>

      <div class="ewin__titlebar">
        <span class="ewin__titlebar-crumb">
          {SUBSYSTEM_LABEL[activeSubsystem]}
        </span>
        <span class="ewin__titlebar-sub">
          {scope.kind === 'assembly' ? 'ASSEMBLY VIEW' : 'PER-VESSEL VIEW'}
        </span>
      </div>

      <div class="ewin__body">
        {#if activeSubsystem === 'sys'}
          <SystemsView {assembly} {scope} />
        {:else if activeSubsystem === 'prop'}
          <PropulsionView {assembly} {scope} />
        {/if}
      </div>

      <footer class="ewin__foot">
        <span class="ewin__foot-k">MODE</span>
        <span class="ewin__foot-v">ENGR</span>
        <span class="ewin__foot-sep"></span>
        <span class="ewin__foot-k">UT</span>
        <span class="ewin__foot-v">Y1 D42 · 04:17:23</span>
        <span class="ewin__foot-spacer"></span>
        <span class="ewin__foot-hint">◄► CYCLE</span>
      </footer>
    </section>
  </div>
{/if}
