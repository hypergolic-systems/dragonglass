<script lang="ts">
  // Owns the module-level `usePartActionWindows` subscription and
  // renders one PartActionWindow per open PAW. Mounted once inside
  // FlightHUD — the host exists so the rune's lifecycle is tied to
  // the flight scene, not the whole app shell.

  import { usePartActionWindows } from '@dragonglass/telemetry/svelte';
  import PartActionWindow from './PartActionWindow.svelte';

  const paws = usePartActionWindows();
</script>

<div class="paw-host" aria-live="polite">
  {#each paws.windows as paw (paw.persistentId)}
    <PartActionWindow
      {paw}
      onClose={() => paws.close(paw.persistentId)}
      onRaise={() => paws.raise(paw.persistentId)}
      onPin={(pin) => paws.setPin(paw.persistentId, pin)}
    />
  {/each}
</div>

<style>
  /* The host container contributes nothing visually — PAWs are
     `position: fixed` and thread their own z-index — but it exists so
     the `{#each}` re-runs in Svelte's tracked scope, and so global
     styles can target it if needed. `display: contents` keeps it out
     of the layout. */
  .paw-host {
    display: contents;
  }
</style>
