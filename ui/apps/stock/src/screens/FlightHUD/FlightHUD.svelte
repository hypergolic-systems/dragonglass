<script lang="ts">
  import { useFlightData, useFlightOps } from '@dragonglass/telemetry/svelte';
  import Navball from './Navball.svelte';
  import CurvedTape from './CurvedTape.svelte';
  import NavballIndicator from './NavballIndicator.svelte';
  import Propulsion from './Propulsion.svelte';
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
</script>

<div class="hud hud--navball-only">
  <div class="navslot navslot--bottom-left">
    <!-- Left-side staging stack. Propulsion is the bottom-most
         occupant today; future staging / fuel / engine-cluster
         displays stack upward on top of it (column-reverse flex,
         anchored to the bottom edge so the row stays aligned with
         the navball baseline). -->
    <div class="staging-stack">
      <Propulsion />
    </div>
    <!-- Navball cluster — explicit 488×488 wrapper so the tape and
         indicator SVGs (which are absolute-positioned inside) still
         contribute that footprint to the parent flex row. Without
         this the flex layout would measure only the <Navball>
         component's intrinsic size and the overlays would float
         loose. -->
    <div class="navball-cluster">
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
</div>
