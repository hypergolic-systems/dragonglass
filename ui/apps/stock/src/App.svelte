<script lang="ts">
  import { setKsp, useGame } from '@dragonglass/telemetry/svelte';
  import { SimulatedKsp } from '@dragonglass/telemetry/simulated';
  import { DragonglassTelemetry } from '@dragonglass/telemetry/dragonglass';
  import type { Ksp } from '@dragonglass/telemetry/core';
  import FlightHUD from './screens/FlightHUD/FlightHUD.svelte';
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
</script>

{#if game.scene === 'FLIGHT'}
  <FlightHUD />
{:else}
  <ScenePlaceholder scene={game.scene} />
{/if}
