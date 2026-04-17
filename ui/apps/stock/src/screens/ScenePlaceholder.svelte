<script lang="ts">
  interface Props {
    scene: string;
  }
  let { scene }: Props = $props();

  const label = $derived(scene ? scene.toLowerCase() : 'connecting…');
</script>

<div class="placeholder">
  <div class="placeholder__brand">DRAGONGLASS</div>
  <div class="placeholder__scene">{label}</div>
</div>

<style>
  .placeholder {
    position: fixed;
    inset: 0;
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: 0.5rem;
    color: #4be0a4;
    font-family: 'Share Tech Mono', ui-monospace, monospace;
    text-transform: uppercase;
    letter-spacing: 0.3em;
    pointer-events: none;
  }
  .placeholder__brand {
    font-size: 1.25rem;
    opacity: 0.6;
  }
  .placeholder__scene {
    font-size: 2rem;
  }
</style>
