// Part Action Windows — reactive list of open PAWs.
//
// One `PawTopic` subscription at the module level opens a new entry
// whenever KSP dispatches a right-click. Each entry owns a
// `PartTopic(id)` subscription until the user closes it, so the
// server only maintains part feeds while they're actually on screen.
//
// Re-right-clicking an already-open part bumps that PAW to the top of
// the stack (so the pilot can find it under other windows) instead of
// opening a duplicate.

import { onDestroy } from 'svelte';
import { getKsp } from './context';
import { PawTopic, PartTopic } from '../core/topics';
import type { PartData } from '../core/part-data';

export interface PartActionWindow {
  readonly persistentId: string;
  /** Live telemetry for this part. `null` until the first frame. */
  data: PartData | null;
  /**
   * Pilot-dragged offset in CSS px from the anchor (the part's
   * current screen position). `null` means "not dragged yet — follow
   * the anchor with the built-in offset".
   */
  pin: { dx: number; dy: number } | null;
  /** Monotonic z-index; higher = on top. */
  z: number;
}

interface InternalPaw extends PartActionWindow {
  /** Unsubscribes from PartTopic(persistentId). */
  unsubscribe: () => void;
}

export interface PartActionWindowOps {
  readonly windows: readonly PartActionWindow[];
  /** Remove a PAW by id and release its subscription. */
  close(persistentId: string): void;
  /**
   * Raise a PAW to the top. Called on mousedown in the window header
   * so dragging brings focus even without reordering DOM.
   */
  raise(persistentId: string): void;
  /** Record a drag offset for a PAW. */
  setPin(persistentId: string, pin: { dx: number; dy: number }): void;
  /**
   * Click a button on a PartModule. Server addresses the target
   * module by its index within `part.Modules` and the event by name.
   */
  invokeEvent(persistentId: string, moduleIndex: number, eventId: string): void;
  /**
   * Write a new value to a KSPField on a PartModule. See
   * `PartOps.setField` for value-type expectations per field kind.
   */
  setField(
    persistentId: string,
    moduleIndex: number,
    fieldId: string,
    value: boolean | number,
  ): void;
  /**
   * Editor-only. Write a new amount to a PartResource — stock's VAB
   * "drag the slider" for fuel loadout. Server clamps to
   * `[0, maxAmount]` and drops the op outside the editor scene.
   */
  setResource(
    persistentId: string,
    resourceName: string,
    amount: number,
  ): void;
}

/**
 * Subscribe to PAW events. Returns reactive window list + ops. Must
 * be called from component initialization (needs Svelte context for
 * the telemetry Ksp handle and the `onDestroy` lifecycle hook for the
 * PawTopic unsubscribe).
 *
 * The returned list is module-local — mounting this hook from more
 * than one component in the same app would duplicate the PawTopic
 * subscription. In practice one `PartActionWindowHost` owns it.
 */
export function usePartActionWindows(): PartActionWindowOps {
  const telemetry = getKsp();
  const windows = $state<InternalPaw[]>([]);
  let zCounter = 1;

  const unsubscribePaw = telemetry.subscribe(PawTopic, (ev) => {
    if (!ev.persistentId) return;  // empty pulse — defensive, shouldn't happen on the wire
    const existing = windows.find((w) => w.persistentId === ev.persistentId);
    if (existing) {
      existing.z = ++zCounter;
      return;
    }
    const persistentId = ev.persistentId;
    // Subscribing to PartTopic(id) is what tells the transport (and
    // thus the server) to spin up the per-part feed. No explicit
    // handshake — the subscribe-on-the-wire signal is driven by the
    // 0 → 1 transition of the callback set, see DragonglassTelemetry.
    const seed: InternalPaw = {
      persistentId,
      data: null,
      pin: null,
      z: ++zCounter,
      unsubscribe: () => {},
    };
    // Re-lookup via `windows.find` inside the frame callback so the
    // mutation goes through Svelte's reactive proxy — writing to the
    // captured `seed` reference directly would bypass the proxy and
    // the template would never re-render.
    seed.unsubscribe = telemetry.subscribe(PartTopic(persistentId), (frame) => {
      const w = windows.find((x) => x.persistentId === persistentId);
      if (!w) return;
      w.data = {
        persistentId: frame.persistentId,
        name: frame.name,
        screen: frame.screen,
        resources: frame.resources,
        modules: frame.modules,
      };
    });
    windows.push(seed);
  });

  onDestroy(() => {
    unsubscribePaw();
    // Each PartTopic unsubscribe triggers the transport's 1 → 0 signal.
    for (const w of windows) w.unsubscribe();
    windows.length = 0;
  });

  function close(persistentId: string): void {
    const idx = windows.findIndex((w) => w.persistentId === persistentId);
    if (idx < 0) return;
    windows[idx].unsubscribe();
    windows.splice(idx, 1);
  }

  function raise(persistentId: string): void {
    const w = windows.find((p) => p.persistentId === persistentId);
    if (w) w.z = ++zCounter;
  }

  function setPin(persistentId: string, pin: { dx: number; dy: number }): void {
    const w = windows.find((p) => p.persistentId === persistentId);
    if (w) w.pin = pin;
  }

  function invokeEvent(persistentId: string, moduleIndex: number, eventId: string): void {
    telemetry.send(PartTopic(persistentId), 'invokeEvent', moduleIndex, eventId);
  }

  function setField(
    persistentId: string,
    moduleIndex: number,
    fieldId: string,
    value: boolean | number,
  ): void {
    telemetry.send(PartTopic(persistentId), 'setField', moduleIndex, fieldId, value);
  }

  function setResource(
    persistentId: string,
    resourceName: string,
    amount: number,
  ): void {
    telemetry.send(PartTopic(persistentId), 'setResource', resourceName, amount);
  }

  return { windows, close, raise, setPin, invokeEvent, setField, setResource };
}
