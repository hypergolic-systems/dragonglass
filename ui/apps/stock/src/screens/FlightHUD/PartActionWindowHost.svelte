<script lang="ts">
  // Owns the module-level `usePartActionWindows` subscription and
  // renders one PartActionWindow per open PAW. Mounted once inside
  // FlightHUD — the host exists so the rune's lifecycle is tied to
  // the flight scene, not the whole app shell.
  //
  // Map-view hide: stock KSP also hides PAWs when the map is open
  // because the screen-space anchor (the part's projected pixel
  // position) isn't meaningful once the world projection is replaced
  // by the orbital map. We keep the subscriptions live so PAWs
  // reappear at the same positions when map view dismisses, rather
  // than closing them and forcing the pilot to re-right-click.

  import { usePartActionWindows, useGame } from '@dragonglass/telemetry/svelte';
  import PartActionWindow from './PartActionWindow.svelte';

  const paws = usePartActionWindows();
  const game = useGame();
</script>

{#if !game.mapActive}
  <div class="paw-host" aria-live="polite">
    {#each paws.windows as paw (paw.persistentId)}
      <PartActionWindow
        {paw}
        onClose={() => paws.close(paw.persistentId)}
        onRaise={() => paws.raise(paw.persistentId)}
        onPin={(pin) => paws.setPin(paw.persistentId, pin)}
        onInvokeEvent={(moduleIndex, eventId) =>
          paws.invokeEvent(paw.persistentId, moduleIndex, eventId)}
        onSetField={(moduleIndex, fieldId, value) =>
          paws.setField(paw.persistentId, moduleIndex, fieldId, value)}
        onSetResource={(name, amount) =>
          paws.setResource(paw.persistentId, name, amount)}
      />
    {/each}
  </div>
{/if}

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
