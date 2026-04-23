<script lang="ts">
  // Minimal editor overlay: just the PAW host + a scene indicator.
  // The VAB/SPH already has its own massively-featured UI (parts bin,
  // action groups, crew assignment); Dragonglass only replaces the
  // right-click info tile here. Flight-only widgets (navball, staging
  // stack, propulsion) stay unmounted so they don't clash with stock's
  // editor chrome.

  import PartActionWindowHost from './FlightHUD/PartActionWindowHost.svelte';

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
