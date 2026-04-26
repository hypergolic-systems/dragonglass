// @dragonglass/windows — floating-window primitives for HUD overlays.
//
// Consumers import the components and dress them with their own
// chrome via the header / default snippets. The package owns drag,
// resize, and z-stacking; visual style is intentionally minimal so
// each panel can carry its own visual language (see Nova's
// VesselPanel or the workbench EngineeringPanel for examples).

export { default as FloatingWindow } from './FloatingWindow.svelte';
export type {
  FloatingWindowPos,
  FloatingWindowSize,
  FloatingWindowProps,
} from './FloatingWindow.svelte';
