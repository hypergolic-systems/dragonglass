// Detect KSP universe-time discontinuities (revert-to-launch,
// revert-to-VAB, revert-to-editor) and surface them as a reactive
// counter. UI state holders that own ephemeral, scene-scoped state
// (open PAWs, staging-card ungroup toggles, dragged window
// positions) watch the counter in a `$effect` and clear themselves
// on each tick — anything tied to "the previous run" of physics
// shouldn't persist into the new run.
//
// Why UT and not scene transitions: a normal flight → space-center
// → flight round trip doesn't usually invalidate the same kinds of
// state, and stock KSP itself behaves the same way (PAWs survive
// pause). The defining property of a revert is that the game
// clock walks backwards while the rest of the world resets to a
// prior point. UT is the cleanest discriminator.
//
// Cadence note: ClockTopic broadcasts at the broadcaster's flush
// rate (10 Hz), so there's a ~100 ms window between revert-fire and
// counter-bump. That's invisible to a human and OK for cleanup —
// the new physics run hasn't even resampled most topics by then.

import { getKsp } from './context';
import { ClockTopic } from '../core/topics';
import { decodeClock } from '../dragonglass/decoders';

interface RevertSignal {
  /**
   * Increments by 1 each time KSP universe time has just walked
   * backward by more than `REVERT_THRESHOLD_S`. Reactive — read in
   * a `$effect` to clear local state when reverts happen.
   */
  readonly revertCount: number;
}

interface MutableSignal {
  revertCount: number;
}

// Anything smaller than this is treated as clock noise / re-syncs
// rather than a real revert. Reverts walk the clock back by many
// seconds at minimum (the revert dialog itself takes a beat to
// resolve, and the "to launch" rollback is typically minutes).
const REVERT_THRESHOLD_S = 0.5;

// Module-level singleton — one signal per app, not per consumer.
// Mirrors the pattern in `useFlightData`: `$state` must be at module
// init time (Svelte 5 disallows it inside `if`), then consumers get
// a stable reactive reference back. The ClockTopic subscription
// itself is lazy so apps that never call useRevertSignal don't pay
// for the signal handling.
const store: MutableSignal = $state<MutableSignal>({ revertCount: 0 });
let subscribed = false;
let lastUt: number | null = null;

export function useRevertSignal(): RevertSignal {
  if (!subscribed) {
    subscribed = true;
    const telemetry = getKsp();
    telemetry.subscribe(ClockTopic, (raw) => {
      const frame = decodeClock(raw);
      if (lastUt !== null && frame.ut < lastUt - REVERT_THRESHOLD_S) {
        store.revertCount += 1;
      }
      lastUt = frame.ut;
    });
  }
  return store as RevertSignal;
}
