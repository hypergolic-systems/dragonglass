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
import { decodePaw, decodePart } from '../dragonglass/decoders';
import type { PartData } from '../core/part-data';
import { Smoothed, vec2Kinematic, type Vec2 } from '../smoothing';
import { useRevertSignal } from './use-revert-signal.svelte';

/**
 * PAWs auto-close when their anchor part drifts farther than this
 * from the active vessel. Comfortably inside KSP's nominal physics
 * range (~2.2 km) so we close before the part unloads, but well
 * outside reasonable intra-vessel anchor separation. See the
 * server-side comment on PartTopic's wire format for why the
 * destruction tombstone alone can't close PAWs in the typical
 * decouple-and-drift case.
 */
const MAX_PAW_DISTANCE_M = 500;

export interface PartActionWindow {
  readonly persistentId: string;
  /** Live telemetry for this part. `null` until the first frame. */
  data: PartData | null;
  /**
   * Smoothed CSS-px screen position of the part anchor. Updated at
   * RAF rate by the shared smoothing registry — values converge
   * toward the wire's `data.screen.x/y` with finite-difference
   * velocity extrapolation, so the leader line stays glued to the
   * part instead of stepping at the 10 Hz wire cadence.
   *
   * Layout consumers should read x/y from this; the truthful
   * "is the part on screen?" flag still lives on `data.screen.visible`.
   */
  readonly screenSmoothed: Vec2;
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
  /** Unsubscribes from PartTopic + the smoothed-position subscription. */
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

  const unsubscribePaw = telemetry.subscribe(PawTopic, (raw) => {
    const ev = decodePaw(raw);
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
    //
    // One smoother per PAW. FD velocity because the wire doesn't
    // carry a screen-velocity field (the screen pos is a projection
    // of the part's 3D world position, computed server-side per
    // tick — there's no derivative on the wire). The smoother
    // registers itself with the shared RAF on first subscriber and
    // unregisters on the last, so a closed PAW costs zero per frame.
    const screenSmoother = new Smoothed<Vec2>(vec2Kinematic, {
      velocity: 'fd',
      tauSec: 0.1,
    });
    const seed: InternalPaw = {
      persistentId,
      data: null,
      screenSmoothed: { x: 0, y: 0 },
      pin: null,
      z: ++zCounter,
      unsubscribe: () => {},
    };
    // Re-lookup via `windows.find` inside the frame callback so the
    // mutation goes through Svelte's reactive proxy — writing to the
    // captured `seed` reference directly would bypass the proxy and
    // the template would never re-render.
    const unsubPart = telemetry.subscribe(PartTopic(persistentId), (raw, tObserved) => {
      const frame = decodePart(raw);
      const w = windows.find((x) => x.persistentId === persistentId);
      if (!w) return;
      // Tombstone: server emits an empty-array frame on `part/<id>`
      // when the underlying KSP Part GameObject is being destroyed
      // (decoupled-and-unloaded past 2.2 km, exploded, editor-deleted).
      // The decoder maps that to `gone: true`. There's no part to
      // anchor to anymore, so close the PAW. Done before we touch
      // any other frame fields because the tombstone frame carries
      // no real state to merge in.
      if (frame.gone) {
        close(persistentId);
        return;
      }
      // Distance auto-close. The destruction tombstone above only
      // fires when the Part GameObject is actually torn down, but
      // KSP's extended ground-loaded area around KSC keeps decoupled
      // stages alive at >2 km. We close the PAW once the anchor part
      // has drifted past `MAX_PAW_DISTANCE_M` from the active vessel
      // — beyond that the PAW can't usefully be looked at anyway,
      // and freeing the topic subscription stops the server sampling
      // it. Threshold is conservative: 500 m is well clear of normal
      // intra-vessel separation but well under physics range.
      if (frame.distanceFromActiveM > MAX_PAW_DISTANCE_M) {
        close(persistentId);
        return;
      }
      w.data = {
        persistentId: frame.persistentId,
        name: frame.name,
        gone: false,
        screen: frame.screen,
        resources: frame.resources,
        modules: frame.modules,
        distanceFromActiveM: frame.distanceFromActiveM,
      };
      // Only feed observations while the projection is meaningful;
      // off-screen frames carry stale or sentinel coordinates.
      if (frame.screen?.visible) {
        screenSmoother.observe(
          { x: frame.screen.x, y: frame.screen.y },
          tObserved,
        );
      }
    });
    // Push smoothed values into the reactive `screenSmoothed`. Per-
    // field assignment so Svelte's `$state` proxy fires reactivity
    // without a per-tick allocation; the smoother's callback buffer
    // is internal and would not be tracked across ticks anyway.
    const unsubSmoothed = screenSmoother.subscribe((pos) => {
      const w = windows.find((x) => x.persistentId === persistentId);
      if (!w) return;
      w.screenSmoothed.x = pos.x;
      w.screenSmoothed.y = pos.y;
    });
    seed.unsubscribe = () => {
      unsubPart();
      unsubSmoothed();
    };
    windows.push(seed);
  });

  // Revert detection. Reverting to launch / VAB / editor walks the
  // KSP universe time backwards; PAWs anchored to the previous run's
  // parts have no sensible meaning afterwards (the parts'
  // persistentIds are reused but the part instances are fresh, the
  // anchor positions reset to t=0 of the new run, and any in-flight
  // ops the user had started would target gone state). Easiest
  // recovery: close every open PAW. The pilot can re-right-click
  // anything they want back.
  const revert = useRevertSignal();
  $effect(() => {
    void revert.revertCount;
    for (const w of windows) w.unsubscribe();
    windows.length = 0;
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
