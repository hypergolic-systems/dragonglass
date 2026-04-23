<script lang="ts">
  import { setKsp, useGame } from '@dragonglass/telemetry/svelte';
  import { SimulatedKsp } from '@dragonglass/telemetry/simulated';
  import { DragonglassTelemetry } from '@dragonglass/telemetry/dragonglass';
  import type { Ksp } from '@dragonglass/telemetry/core';
  import FlightHUD from './screens/FlightHUD/FlightHUD.svelte';
  import EditorHUD from './screens/EditorHUD.svelte';
  import ScenePlaceholder from './screens/ScenePlaceholder.svelte';

  // The sidecar launches us with `?ws=ws://127.0.0.1:8787/` to attach
  // to the live KSP telemetry feed. Without the param (e.g. `just
  // ui-dev` in a plain browser) we fall back to the keyboard-driven
  // SimulatedKsp so the navball and tapes still animate for UI work.
  const wsUrl = new URLSearchParams(window.location.search).get('ws');
  const ksp: Ksp = wsUrl
    ? new DragonglassTelemetry(wsUrl)
    : new SimulatedKsp();
  ksp.connect();
  setKsp(ksp);

  const game = useGame();

  // Suppress the browser's default right-click context menu across
  // the whole HUD. CEF's built-in menu (Inspect Element, Copy, etc.)
  // is irrelevant in a KSP overlay and would visually conflict with
  // our in-HUD context menus. Components that want a menu on
  // right-click handle `contextmenu` themselves (with their own
  // preventDefault in line); this listener only catches the ones
  // that don't so nothing leaks through.
  document.addEventListener('contextmenu', (e) => e.preventDefault());
</script>

{#if game.scene === 'FLIGHT'}
  <FlightHUD />
{:else if game.scene === 'EDITOR'}
  <EditorHUD scene={game.scene} />
{:else}
  <ScenePlaceholder scene={game.scene} />
{/if}
