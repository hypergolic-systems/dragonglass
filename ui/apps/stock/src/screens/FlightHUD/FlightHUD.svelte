<script lang="ts">
  import { useFlightData, useFlightOps } from '@dragonglass/telemetry/svelte';
  import {
    Navball,
    CurvedTape,
    NavballIndicator,
    Propulsion,
    StagingStack,
    PunchThrough,
    PunchThroughProvider,
    formatSurfaceSpeed,
    formatAltitude,
    SPEED_SCALE,
    ALTITUDE_SCALE,
  } from '@dragonglass/instruments';
  import PartActionWindowHost from './PartActionWindowHost.svelte';
  import '@dragonglass/instruments/flight.css';

  const s = useFlightData();
  const ops = useFlightOps();

  // Speed tape is driven by whichever velocity reference KSP's own
  // speed-display mode has selected (SURFACE / ORBIT / TARGET). The
  // label mirrors that — pilot muscle memory is built around the
  // stock readout, so the tape must agree with the mode the pilot
  // sees elsewhere in the game.
  const speedVector = $derived(
    s.speedDisplayMode === 'orbit' ? s.orbitalVelocity
    : s.speedDisplayMode === 'target' ? s.targetVelocity
    : s.surfaceVelocity,
  );
  const speed = $derived(speedVector.length());
  const speedLabel = $derived(
    s.speedDisplayMode === 'orbit' ? 'ORBIT'
    : s.speedDisplayMode === 'target' ? 'TARGET'
    : 'SURFACE',
  );
</script>

<PunchThroughProvider>
<div class="hud hud--navball-only">
  <!-- Punch-through preview slot. Bottom-right corner placeholder for
       the future Kerbal portrait gallery — the rect tracks here, so a
       mod-side stream registered under id "test" (e.g. a checkerboard
       from the plugin) shows through the chroma-key. With no stream
       registered this just renders as the chroma color. -->
  <div class="punch-preview">
    <PunchThrough id="test" />
  </div>

  <div class="navslot navslot--bottom-left">
    <!-- Left-side staging stack. Propulsion sits at the bottom
         (DOM-first under column-reverse); StagingStack stacks above
         it showing one card per operating stage. -->
    <div class="staging-stack">
      <Propulsion />
      <StagingStack />
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
        value={speed}
        modeLabel={speedLabel}
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

  <!-- Part Action Windows — draggable info tiles spawned on the
       right-click of a part. Mounted here so the host unmounts with
       the flight scene, taking every PAW subscription with it. -->
  <PartActionWindowHost />
</div>
</PunchThroughProvider>

<style>
  .punch-preview {
    position: fixed;
    right: 24px;
    bottom: 24px;
    width: 180px;
    height: 180px;
    border: 1px solid rgba(255, 255, 255, 0.25);
    border-radius: 4px;
    overflow: hidden;
    pointer-events: none;
  }
</style>
