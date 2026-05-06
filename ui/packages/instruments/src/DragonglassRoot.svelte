<script lang="ts">
  // The single component every Dragonglass HUD app wraps its tree in.
  //
  // Bundles the host-coupling concerns that are identical across stock
  // and third-party HUDs (e.g. nova) so a downstream app's entry just
  // looks like:
  //
  //   <DragonglassRoot>
  //     <MyAppContent />
  //   </DragonglassRoot>
  //
  // What lives here:
  //
  //  - <PunchThroughProvider> — mounts the encoded-row canvas and the
  //    rect registry so any descendant <PunchThrough> works. Self-gates
  //    on `isHostKsp()` so it's a no-op in dev / vanilla browser tabs.
  //
  //  - contextmenu suppression — CEF's built-in menu (Inspect Element,
  //    Copy, …) is meaningless in a KSP overlay and would visually
  //    conflict with in-HUD context menus. Components that want a menu
  //    on right-click handle `contextmenu` themselves with their own
  //    preventDefault inline; this listener catches the rest.
  //
  // What does NOT live here (intentionally — these are app concerns,
  // not host concerns):
  //
  //  - Telemetry connect / capability declaration — every mod app
  //    declares its own caps.
  //  - Scene routing — every mod's scene ↔ component map is its own.
  //  - Theming / CSS resets — apps own their visual identity.
  //
  // When we add more host-coupling concerns later (cursor management,
  // shared focus handling, …) they land here so consumers don't have
  // to update their wiring.

  import { onMount } from 'svelte';
  import PunchThroughProvider from './flight/PunchThroughProvider.svelte';

  let { children }: { children?: import('svelte').Snippet } = $props();

  onMount(() => {
    const block = (e: Event) => e.preventDefault();
    document.addEventListener('contextmenu', block);
    return () => document.removeEventListener('contextmenu', block);
  });
</script>

<PunchThroughProvider>
  {@render children?.()}
</PunchThroughProvider>
