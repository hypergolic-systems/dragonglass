<script lang="ts">
  import { getKsp } from '@dragonglass/telemetry/svelte';
  import { GameTopic } from '@dragonglass/telemetry/core';

  // Workbench is experimental and doesn't replace any stock KSP chrome
  // yet — declare an empty capability set so the plugin leaves stock's
  // navball, parts panel, PAWs, etc. fully intact underneath.
  const ksp = getKsp();
  ksp.connect().then(() => {
    ksp.send(GameTopic, 'setCapabilities', []);
  });
</script>

<div class="workbench">Dragonglass Workbench</div>

<style>
  .workbench {
    color: var(--fg);
    padding: 32px;
    font-family: var(--font-mono);
  }
</style>
