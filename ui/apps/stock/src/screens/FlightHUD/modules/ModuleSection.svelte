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

<Renderer {module} {onInvokeEvent} {onSetField} />
