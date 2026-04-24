<script lang="ts">
  import { setKsp } from '@dragonglass/telemetry/svelte';
  import { SimulatedKsp } from '@dragonglass/telemetry/simulated';
  import { DragonglassTelemetry } from '@dragonglass/telemetry/dragonglass';
  import { GameTopic, type Ksp } from '@dragonglass/telemetry/core';

  // Workbench is experimental and doesn't replace any stock KSP chrome
  // yet — declare an empty capability set so the plugin leaves stock's
  // navball, parts panel, PAWs, etc. fully intact underneath.
  const wsUrl = new URLSearchParams(window.location.search).get('ws');
  const ksp: Ksp = wsUrl
    ? new DragonglassTelemetry(wsUrl)
    : new SimulatedKsp();
  ksp.connect().then(() => {
    ksp.send(GameTopic, 'setCapabilities', []);
  });
  setKsp(ksp);
</script>

<div class="workbench">Dragonglass Workbench</div>

<style>
  .workbench {
    color: var(--fg);
    padding: 32px;
    font-family: var(--font-mono);
  }
</style>
