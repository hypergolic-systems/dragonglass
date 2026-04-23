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
  };

  const Renderer = $derived(RENDERERS[module.kind] ?? DefaultModule);
</script>

<Renderer {module} {onInvokeEvent} {onSetField} />
