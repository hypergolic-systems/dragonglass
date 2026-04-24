<script lang="ts">
  // Dispatcher for per-PartModule rendering.
  //
  // Renderers are keyed by `module.kind` — the wire-level
  // discriminator the server sets based on the PartModule's C# type
  // (`ModuleEngines` → `'engines'`, everything else → `'generic'`).
  // That way multiple KSP classes that share a typed shape (e.g.
  // ModuleEngines + ModuleEnginesFX via inheritance) hit the same
  // renderer without the UI knowing about each variant.
  //
  // Adding a new bespoke renderer = add a typed wire kind on the
  // server + a typed variant in `PartModuleData` + one entry below
  // + one new .svelte file. Modules without a typed kind stay on
  // `DefaultModule` and get the generic event-buttons-plus-field-
  // widgets layout.

  import type { Component } from 'svelte';
  import DefaultModule from './DefaultModule.svelte';
  import EngineModule from './EngineModule.svelte';
  import SensorModule from './SensorModule.svelte';
  import ScienceModule from './ScienceModule.svelte';
  import SolarPanelModule from './SolarPanelModule.svelte';
  import GeneratorModule from './GeneratorModule.svelte';
  import LightModule from './LightModule.svelte';
  import ParachuteModule from './ParachuteModule.svelte';
  import CommandModule from './CommandModule.svelte';
  import ReactionWheelModule from './ReactionWheelModule.svelte';
  import RcsModule from './RcsModule.svelte';
  import DecouplerModule from './DecouplerModule.svelte';
  import DataTransmitterModule from './DataTransmitterModule.svelte';
  import DeployableAntennaModule from './DeployableAntennaModule.svelte';
  import DeployableRadiatorModule from './DeployableRadiatorModule.svelte';
  import ActiveRadiatorModule from './ActiveRadiatorModule.svelte';
  import ResourceHarvesterModule from './ResourceHarvesterModule.svelte';
  import ResourceConverterModule from './ResourceConverterModule.svelte';
  import ControlSurfaceModule from './ControlSurfaceModule.svelte';
  import AlternatorModule from './AlternatorModule.svelte';
  import ModuleGlyph from './ModuleGlyph.svelte';
  import type { ModuleRendererProps } from './types';

  const { module, onInvokeEvent, onSetField }: ModuleRendererProps = $props();

  const RENDERERS: Record<string, Component<ModuleRendererProps>> = {
    engines: EngineModule,
    sensor: SensorModule,
    science: ScienceModule,
    solar: SolarPanelModule,
    generator: GeneratorModule,
    light: LightModule,
    parachute: ParachuteModule,
    command: CommandModule,
    reactionWheel: ReactionWheelModule,
    rcs: RcsModule,
    decoupler: DecouplerModule,
    transmitter: DataTransmitterModule,
    deployAntenna: DeployableAntennaModule,
    deployRadiator: DeployableRadiatorModule,
    activeRadiator: ActiveRadiatorModule,
    harvester: ResourceHarvesterModule,
    converter: ResourceConverterModule,
    controlSurface: ControlSurfaceModule,
    alternator: AlternatorModule,
  };

  const Renderer = $derived(RENDERERS[module.kind] ?? DefaultModule);
</script>

<!-- Wrapper provides a relative-positioning context for the decorative
     glyph stamp. The stamp sits behind the renderer at low opacity so
     it doesn't compete with live readouts; per-module sections already
     carry their own padding + top border, so the wrapper itself is
     layout-neutral. -->
<div class="m-wrap" data-kind={module.kind}>
  <ModuleGlyph kind={module.kind} />
  <Renderer {module} {onInvokeEvent} {onSetField} />
</div>

<style>
  .m-wrap {
    position: relative;
  }
  /* Hoist the renderer above the decorative stamp. The stamp is
     z:0, the renderer's direct child section lives at z:1. Without
     this the ModuleGlyph would overlap live event buttons near the
     top-right of each module. */
  .m-wrap :global(> section) {
    position: relative;
    z-index: 1;
  }

  /* Breathing pulse for any active-tone badge across every module
     renderer. Rather than teach each renderer about the animation,
     we target the [data-tone='active'] attribute every module sets
     on its header badge — the design contract is established. The
     animation is a 3.2s slow phosphor breath; deliberately longer
     than the player's scan cadence so it reads as ambient life rather
     than an attention-grab. */
  @keyframes phosphor-breath {
    0%, 100% {
      text-shadow: 0 0 3px var(--accent-glow);
      box-shadow: inset 0 0 0 0 rgba(126, 245, 184, 0);
    }
    50% {
      text-shadow: 0 0 7px var(--accent-soft), 0 0 14px var(--accent-glow);
      box-shadow: inset 0 0 10px rgba(126, 245, 184, 0.18);
    }
  }
  .m-wrap :global([data-tone='active']),
  .m-wrap :global([data-status='burning']) {
    animation: phosphor-breath 3.2s ease-in-out infinite;
  }
  /* Respect users who've opted out of motion — drop the pulse so
     the badge stays in the glow-on snapshot without animating. */
  @media (prefers-reduced-motion: reduce) {
    .m-wrap :global([data-tone='active']),
    .m-wrap :global([data-status='burning']) {
      animation: none;
    }
  }
</style>
