<script lang="ts">
  // Editor overlay: PAW host + staging stack + a scene indicator.
  // The VAB/SPH keeps its own parts bin / action groups / crew
  // assignment; we stop at right-click info tiles and the staging
  // pane. Flight-only widgets (navball, propulsion readouts) stay
  // unmounted — they assume a live vessel with a burning throttle,
  // which has no meaning on the assembly floor.

  import PartActionWindowHost from './FlightHUD/PartActionWindowHost.svelte';
  import StagingStack from './FlightHUD/StagingStack.svelte';
  import './FlightHUD/FlightHUD.css';

  interface Props {
    scene: string;
  }
  const { scene }: Props = $props();
  const label = $derived(scene === 'EDITOR' ? 'VAB / SPH' : scene.toLowerCase());
</script>

<div class="editor-hud">
  <div class="editor-hud__badge" aria-hidden="true">
    <span class="editor-hud__brand">DRAGONGLASS</span>
    <span class="editor-hud__scene">{label}</span>
  </div>

  <!-- Stage stack, pinned bottom-right to mirror stock's editor stager
       placement. The StagingStack component is reused verbatim — it
       only consumes StageTopic which the server already mirrors for
       editor scenes — so drag-reorder and icon interactions come
       along for free. -->
  <div class="navslot navslot--bottom-right">
    <div class="staging-stack">
      <StagingStack />
    </div>
  </div>

  <!-- Mounting the PAW host here means the PawTopic subscription and
       every open PAW unmount when the editor exits — the same
       scene-tied lifecycle as FlightHUD. -->
  <PartActionWindowHost />
</div>

<style>
  .editor-hud {
    position: fixed;
    inset: 0;
    pointer-events: none;
  }
  .editor-hud__badge {
    position: absolute;
    top: 10px;
    right: 14px;
    display: flex;
    flex-direction: column;
    align-items: flex-end;
    gap: 2px;
    color: var(--fg-dim, #4be0a4);
    font-family: 'Share Tech Mono', ui-monospace, monospace;
    text-transform: uppercase;
    letter-spacing: 0.22em;
    opacity: 0.35;
  }
  .editor-hud__brand {
    font-size: 9px;
  }
  .editor-hud__scene {
    font-size: 11px;
    color: var(--accent, #4be0a4);
  }
</style>
