<script lang="ts">
  // One draggable FloatingWindow per active Kerbal. Each window
  // hosts a `<PunchThrough id="kerbal:<name>">`; the native plugin's
  // chroma-key compositor reveals the live `Kerbal.avatarTexture`
  // (256² IVA RenderTexture) underneath wherever the user has parked
  // the window.
  //
  // Rather than the stock single-strip gallery, every crew member
  // gets their own fully-resizable, fully-position-persisted window.
  // Positions and z-stacking live in a module-level map keyed by
  // stream id so a Kerbal who leaves the active vessel and comes
  // back (transfer, EVA, vessel switch) returns to their last
  // pinned position.

  import { onDestroy } from 'svelte';
  import { getKsp } from '@dragonglass/telemetry/svelte';
  import {
    PortraitsTopic,
    type PortraitsWire,
    type PortraitEntryWire,
  } from '@dragonglass/telemetry/core';
  import { PunchThrough } from '@dragonglass/instruments';
  import { FloatingWindow } from '@dragonglass/windows';

  type Entry = {
    id: string;
    name: string;
    role: string;
    level: number;
  };

  const ksp = getKsp();
  let entries = $state<Entry[]>([]);

  const unsubscribe = ksp.subscribe(PortraitsTopic, (raw: PortraitsWire) => {
    const list: PortraitEntryWire[] = raw[0] ?? [];
    entries = list.map(([id, name, role, level]) => ({ id, name, role, level }));
  });
  onDestroy(unsubscribe);

  // Per-portrait z-stacking. Each `onRaise` bumps a global counter
  // and stamps it on the raised window. Kerbals not yet raised get
  // a low default so newly-spawned windows appear above old ones
  // unless the pilot has been actively raising things.
  let zTop = $state(100);
  const zMap = $state<Record<string, number>>({});

  function raise(id: string): void {
    zTop += 1;
    zMap[id] = zTop;
  }

  function zFor(id: string): number {
    return zMap[id] ?? 100;
  }

  // Default initial position for a new portrait. Cascade them along
  // the bottom-right so the first 6 land in a strip; further crew
  // overlap and can be dragged out by the pilot. Window dimensions
  // chosen to give ~160² of visible portrait plus a slim header.
  const WIN_W = 168;
  const WIN_H = 192;
  const STRIP_GAP = 12;
  const STRIP_RIGHT = 24;
  const STRIP_BOTTOM = 24;

  function defaultPos(index: number): { x: number; y: number } {
    return {
      x: window.innerWidth - STRIP_RIGHT - (index + 1) * (WIN_W + STRIP_GAP) + STRIP_GAP,
      y: window.innerHeight - STRIP_BOTTOM - WIN_H,
    };
  }

  function shortName(n: string): string {
    const i = n.indexOf(' ');
    return i < 0 ? n : n.slice(0, i);
  }
</script>

{#each entries as entry, i (entry.id)}
  <FloatingWindow
    title={entry.name}
    defaultPos={defaultPos(i)}
    defaultSize={{ w: WIN_W, h: WIN_H }}
    minSize={{ w: 120, h: 144 }}
    z={zFor(entry.id)}
    onRaise={() => raise(entry.id)}
  >
    {#snippet header()}
      <div class="portrait-header">
        <span class="portrait-header__name">{shortName(entry.name)}</span>
        <span class="portrait-header__role">{entry.role}</span>
      </div>
    {/snippet}
    <div class="portrait-body">
      <PunchThrough id={entry.id} />
    </div>
  </FloatingWindow>
{/each}

<style>
  .portrait-header {
    flex: 1 1 auto;
    display: flex;
    align-items: baseline;
    gap: 8px;
    padding: 4px 8px;
    background: rgba(20, 22, 28, 0.85);
    color: #f0f0f0;
    font-size: 12px;
    line-height: 1.2;
    /* Header is the drag grip — covers the full top edge. */
    cursor: grab;
  }
  .portrait-header__name {
    font-weight: 600;
    letter-spacing: 0.04em;
  }
  .portrait-header__role {
    flex: 1 1 auto;
    opacity: 0.7;
    font-size: 10px;
    letter-spacing: 0.06em;
    text-transform: uppercase;
  }

  /* The body fills the remaining FloatingWindow area; the
     PunchThrough placeholder paints the chroma color across that
     full region, and the native compositor reveals the live IVA
     portrait there. */
  .portrait-body {
    width: 100%;
    height: 100%;
    background: rgba(20, 22, 28, 0.85);
  }

  /* Style the FloatingWindow chrome via :global so the consumer
     (this gallery) can give portrait windows a distinct visual
     vocabulary without forking the FloatingWindow primitive. */
  :global(.fw):has(.portrait-body) {
    border: 1px solid rgba(120, 130, 145, 0.45);
    border-radius: 4px;
    box-shadow: 0 4px 16px rgba(0, 0, 0, 0.55);
    overflow: hidden;
  }
</style>
