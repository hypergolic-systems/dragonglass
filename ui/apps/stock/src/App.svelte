<script lang="ts">
  import { setKsp } from '@dragonglass/telemetry/svelte';
  import { SimulatedKsp } from '@dragonglass/telemetry/simulated';
  import FlightHUD from './screens/FlightHUD/FlightHUD.svelte';

  const ksp = new SimulatedKsp();
  ksp.connect();
  setKsp(ksp);
</script>

<FlightHUD />
