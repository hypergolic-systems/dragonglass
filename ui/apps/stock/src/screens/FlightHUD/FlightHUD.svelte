<script lang="ts">
  import { useFlightData } from '@dragonglass/telemetry/svelte';
  import Navball from './Navball.svelte';
  import CurvedTape from './CurvedTape.svelte';
  import { formatSurfaceSpeed, formatAltitude } from './format';
  import { SPEED_SCALE, ALTITUDE_SCALE } from './tape-scales';
  import './FlightHUD.css';

  const s = useFlightData();

  let clickCount = $state(0);
</script>

<div class="hud hud--navball-only">
  <!-- Temporary: input test button. Remove after verifying SHM input pipeline. -->
  <button
    class="input-test"
    class:input-test--clicked={clickCount > 0}
    onclick={() => clickCount++}
  >
    {clickCount === 0 ? 'CLICK ME' : `CLICKED ×${clickCount}`}
  </button>

  <div class="navslot navslot--bottom-left">
    <Navball orientation={s.orientation} />
    <CurvedTape
      side="left"
      value={s.surfaceVelocity}
      modeLabel="SURFACE"
      scale={SPEED_SCALE}
      formatReadout={formatSurfaceSpeed}
    />
    <CurvedTape
      side="right"
      value={s.altitude}
      modeLabel="ALT"
      scale={ALTITUDE_SCALE}
      formatReadout={formatAltitude}
    />
  </div>
</div>
