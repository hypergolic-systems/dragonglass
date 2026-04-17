<script lang="ts">
  import type { AssemblyModel } from '@dragonglass/telemetry/core';
  import type { Scope } from './power';
  import PowerAccordion from './PowerAccordion.svelte';
  import SubsystemStub from './SubsystemStub.svelte';

  let { assembly, scope }: { assembly: AssemblyModel; scope: Scope } = $props();
</script>

<div class="sys">
  <PowerAccordion {assembly} {scope} />
  <SubsystemStub label="THERMAL" short="THR" statusLabel="NOMINAL" statusDetail="NO RADIATOR ALERTS · Δ 54 K / −12 K" />
  <SubsystemStub label="COMMS" short="COM" statusLabel="LINKED" statusDetail="SIG 98% · 1 RELAY · DSN 0.4 Gm" />
</div>
