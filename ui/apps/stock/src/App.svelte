<script lang="ts">
  import { getKsp, useGame } from '@dragonglass/telemetry/svelte';
  import {
    GameTopic,
    ConfigTopic,
    CAP_FLIGHT_UI,
    CAP_FLIGHT_PAW,
    CAP_EDITOR_PARTS,
    CAP_EDITOR_PAW,
    type Capability,
    type ConfigData,
  } from '@dragonglass/telemetry/core';
  import FlightHUD from './screens/FlightHUD/FlightHUD.svelte';
  import EditorHUD from './screens/EditorHUD.svelte';
  import ScenePlaceholder from './screens/ScenePlaceholder.svelte';

  // Stock's view of <modDir>/config.json. Every key is optional; a
  // missing key behaves as `true`. `editor: false` hides the whole
  // editor HUD and also withholds the editor/* caps so stock KSP
  // renders its own editor chrome; `paw: false` withholds the PAW
  // caps so stock KSP PAWs stay live.
  interface StockConfig {
    editor?: boolean;
    paw?: boolean;
  }

  // Telemetry singleton; auto-bootstraps from `?ws=` (sidecar-set in
  // KSP, absent under `just ui-dev` → SimulatedKsp).
  const ksp = getKsp();

  // Config arrives asynchronously: subscribe, wait for the first
  // frame, then translate it into the capability declaration. The
  // plugin retains the frame for snapshot-on-connect replay so this
  // resolves in one round-trip. Reactive `$state` so conditional UI
  // gates (editor HUD) pick up the value without explicit wiring.
  let config: StockConfig = $state({});
  let capsSent = false;
  ksp.connect().then(() => {
    ksp.subscribe(ConfigTopic, (raw: ConfigData) => {
      config = raw as StockConfig;
      if (capsSent) return;
      capsSent = true;
      ksp.send(GameTopic, 'setCapabilities', computeCaps(config));
    });
  });

  // `editor: false` suppresses everything editor-scoped, including
  // editor/paw even when `paw: true` — the UI isn't mounting an
  // editor at all, so an editor PAW has nothing to live in.
  function computeCaps(cfg: StockConfig): Capability[] {
    const editorOn = cfg.editor !== false;
    const pawOn = cfg.paw !== false;
    const caps: Capability[] = [CAP_FLIGHT_UI];
    if (pawOn) caps.push(CAP_FLIGHT_PAW);
    if (editorOn) {
      caps.push(CAP_EDITOR_PARTS);
      if (pawOn) caps.push(CAP_EDITOR_PAW);
    }
    return caps;
  }

  const game = useGame();

  // Suppress the browser's default right-click context menu across
  // the whole HUD. CEF's built-in menu (Inspect Element, Copy, etc.)
  // is irrelevant in a KSP overlay and would visually conflict with
  // our in-HUD context menus. Components that want a menu on
  // right-click handle `contextmenu` themselves (with their own
  // preventDefault in line); this listener only catches the ones
  // that don't so nothing leaks through.
  document.addEventListener('contextmenu', (e) => e.preventDefault());
</script>

{#if game.scene === 'FLIGHT'}
  <FlightHUD />
{:else if game.scene === 'EDITOR'}
  {#if config.editor !== false}
    <EditorHUD scene={game.scene} />
  {/if}
{:else}
  <ScenePlaceholder scene={game.scene} />
{/if}
