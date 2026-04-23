<script lang="ts">
  // Generic fallback renderer for any PartModule the registry doesn't
  // specialise — covers the long tail of KSP modules (ModuleLight,
  // ModuleParachute, ModuleDecouplerBase, ...).
  //
  // Layout:
  //   ┌─ MODULE NAME ─────────────────────┐
  //   │ [ EVENT BUTTON ]  [ EVENT BUTTON ] │  ← clickable events at
  //   │ <field widget>                     │    the top, then each
  //   │ <field widget>                     │    KSPField rendered by
  //   └────────────────────────────────────┘    its kind (toggle /
  //                                              slider / option /
  //                                              numeric / progress /
  //                                              label fallback).
  //
  // A bespoke renderer replaces this entirely (the dispatcher in
  // `ModuleSection.svelte` swaps the component) so it can show a
  // totally different visual — e.g. an engine rosette with fuel
  // bars instead of buttons and rows.

  import type { PartModuleGeneric } from '@dragonglass/telemetry/core';
  import { prettyModuleName } from './module-name';
  import FieldWidget from './widgets/FieldWidget.svelte';
  import type { ModuleRendererProps } from './types';

  // The dispatcher in `ModuleSection.svelte` only routes
  // kind='generic' modules here, so narrow to the generic shape for
  // direct access to `events` / `fields`. Typed modules have bespoke
  // renderers and carry no generic arrays.
  const { module, onInvokeEvent, onSetField }: ModuleRendererProps = $props();
  const generic = $derived(module as PartModuleGeneric);
  const title = $derived(prettyModuleName(generic.moduleName));
</script>

<section class="mod">
  <header class="mod__head">
    <span class="mod__name">{title}</span>
  </header>

  {#if generic.events.length > 0}
    <div class="mod__events">
      {#each generic.events as event (event.id)}
        <button
          type="button"
          class="mod__event"
          onclick={() => onInvokeEvent(event.id)}
          onpointerdown={(e) => e.stopPropagation()}
        >{event.label}</button>
      {/each}
    </div>
  {/if}

  {#if generic.fields.length > 0}
    <div class="mod__fields">
      {#each generic.fields as field (field.id)}
        <FieldWidget {field} {onSetField} />
      {/each}
    </div>
  {/if}
</section>

<style>
  .mod {
    margin-top: 8px;
    padding-top: 7px;
    border-top: 1px solid var(--line);
  }

  .mod__head {
    display: flex;
    align-items: center;
    margin-bottom: 5px;
  }

  .mod__name {
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.22em;
    color: var(--fg-dim);
    text-transform: uppercase;
  }

  /* Button row wraps to a second line on narrow panels. Buttons are
     compact but tappable — min 24 px high for a reliable click
     target, padding tuned so two-word labels fit one line on the
     panel's 204-px body width. */
  .mod__events {
    display: flex;
    flex-wrap: wrap;
    gap: 4px;
    margin-bottom: 6px;
  }

  .mod__event {
    padding: 3px 8px;
    min-height: 22px;
    font-family: var(--font-mono);
    font-size: 9px;
    letter-spacing: 0.08em;
    text-transform: uppercase;
    color: var(--accent);
    background: rgba(126, 245, 184, 0.06);
    border: 1px solid var(--line-accent);
    cursor: pointer;
    transition:
      background 160ms ease,
      border-color 160ms ease,
      color 160ms ease;
  }
  .mod__event:hover {
    background: rgba(126, 245, 184, 0.16);
    border-color: var(--accent);
    color: var(--accent-soft);
  }
  .mod__event:active {
    background: rgba(126, 245, 184, 0.28);
  }

  /* Field widgets stack vertically with a small gap; each widget
     owns its own inner layout. Keeping the outer container minimal
     means adding a new field kind doesn't require touching this
     container's CSS — just drop in another FieldWidget branch. */
  .mod__fields {
    display: flex;
    flex-direction: column;
    gap: 4px;
  }
</style>
