<script lang="ts">
  import { useFlightData, useFlightOps } from '@dragonglass/telemetry/svelte';
  import Navball from './Navball.svelte';
  import CurvedTape from './CurvedTape.svelte';
  import NavballIndicator from './NavballIndicator.svelte';
  import { formatSurfaceSpeed, formatAltitude } from './format';
  import { SPEED_SCALE, ALTITUDE_SCALE } from './tape-scales';
  import './FlightHUD.css';

  const s = useFlightData();
  const ops = useFlightOps();

  // Derived speeds: surface-relative speed (shown by default on the
  // tape) and orbital speed (available for a future SURFACE/ORBIT
  // toggle). Reading fields off the reactive `s` proxy inside $derived
  // makes the tape re-render only when the vector actually changes.
  const surfaceSpeed = $derived(s.surfaceVelocity.length());

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
    <Navball />
    <CurvedTape
      side="left"
      value={surfaceSpeed}
      modeLabel="SURFACE"
      scale={SPEED_SCALE}
      formatReadout={formatSurfaceSpeed}
    />
    <CurvedTape
      side="right"
      value={s.altitudeAsl}
      modeLabel="ALT"
      scale={ALTITUDE_SCALE}
      formatReadout={formatAltitude}
    />
    <NavballIndicator
      kind="rcs"
      active={s.rcs}
      onclick={() => ops.setRcs(!s.rcs)}
    />
    <NavballIndicator
      kind="sas"
      active={s.sas}
      onclick={() => ops.setSas(!s.sas)}
    />
  </div>
</div>
