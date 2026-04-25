// @dragonglass/instruments — flight instrument component library.
//
// Components and supporting helpers for composing a KSP flight HUD.
// Stock's FlightHUD and any third-party UI mod (e.g. Nova) consume
// these via the @dragonglass/instruments importmap specifier.

export { default as Navball } from './flight/Navball.svelte';
export { default as NavballIndicator } from './flight/NavballIndicator.svelte';
export { default as CurvedTape } from './flight/CurvedTape.svelte';
export { default as StagingStack } from './flight/StagingStack.svelte';
export { default as Propulsion } from './flight/Propulsion.svelte';

export {
  formatSurfaceSpeed,
  formatAltitude,
  formatAltLabel,
  formatSpeedLabel,
  formatDeltaV,
  formatTwr,
  formatThrust,
} from './flight/format';

export {
  SPEED_SCALE,
  ALTITUDE_SCALE,
  type TapeScale,
} from './flight/tape-scales';
