// Shared prop shape for per-PartModule renderers. Every renderer
// (default or bespoke) receives these three values: the module data,
// a callback to invoke a KSPEvent, and a callback to write a
// KSPField. Bespoke renderers can of course pick and choose which
// parts of the module to show or ignore.

import type { PartModuleData } from '@dragonglass/telemetry/core';

export interface ModuleRendererProps {
  module: PartModuleData;
  onInvokeEvent: (eventId: string) => void;
  onSetField: (fieldId: string, value: boolean | number) => void;
}
