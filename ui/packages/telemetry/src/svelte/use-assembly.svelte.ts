import { getKsp } from './context';
import { AssemblyTopic } from '../core/topics';
import type { AssemblyModel } from '../core/assembly';

/**
 * Subscribe to the assembly telemetry topic. Returns a reactive
 * reference that updates when the vessel structure changes (~1 Hz).
 *
 * Must be called during component initialization (needs Svelte context).
 */
export function useAssembly(): { current: AssemblyModel | undefined } {
  const telemetry = getKsp();
  let current = $state<AssemblyModel | undefined>(undefined);

  $effect(() => {
    return telemetry.subscribe(AssemblyTopic, (frame) => {
      current = frame;
    });
  });

  return {
    get current() { return current; },
  };
}
