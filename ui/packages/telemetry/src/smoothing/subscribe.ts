// Topic ↔ Smoothed glue.
//
// Wraps a `Smoothed<T,V>` around a topic subscription: every wire
// frame is `select()`-ed into a `(value, velocity?)` pair and fed to
// the smoother's `observe()`. The caller's callback receives smoothed
// values at RAF rate from the shared `SmoothedRegistry`.
//
// One `Smoothed` per `subscribeSmoothed` call. No sharing across
// callers — that's the topic-subscription layer's responsibility, not
// ours; if the same topic ends up wired to N smoothed callers, each
// gets their own `Smoothed` and runs its own math. A future change to
// dedupe at the topic layer (so N raw subscribers share one frame
// reference) would benefit smoothing transparently.

import type { Ksp, Topic } from '../core/ksp';
import type { Kinematic, SmoothingOptions } from './kinematic';
import { Smoothed } from './smoothed';

export interface SmoothedTopicConfig<Raw, T, V> {
  /**
   * Project a raw frame into the smoother's value (and optionally
   * its velocity, when the wire carries one). Return `null` to
   * skip this frame entirely — useful when a frame is "uninteresting"
   * for smoothing purposes (e.g. the underlying entity has gone
   * off-screen and the position field is meaningless).
   */
  select: (raw: Raw) => { value: T; velocity?: V } | null;

  kinematic: Kinematic<T, V>;
  options: SmoothingOptions<T>;
}

/**
 * Subscribe to a topic with smoothing. The callback fires at RAF
 * rate (driven by `SmoothedRegistry`) with the latest smoothed
 * value. The buffer passed to the callback is the smoother's
 * internal scratch — do not retain across calls; copy fields you
 * want to snapshot.
 *
 * Returns an unsubscribe function that detaches both the inner
 * topic subscription and the push-mode subscriber.
 */
export function subscribeSmoothed<Raw, T, V = T>(
  ksp: Ksp,
  topic: Topic<Raw>,
  config: SmoothedTopicConfig<Raw, T, V>,
  cb: (smoothed: T) => void,
): () => void {
  const smoothed = new Smoothed<T, V>(config.kinematic, config.options);

  const unsubTopic = ksp.subscribe(topic, (frame, tObserved) => {
    const projected = config.select(frame);
    if (projected === null) return;
    smoothed.observe(projected.value, tObserved, projected.velocity);
  });

  const unsubPush = smoothed.subscribe(cb);

  return () => {
    unsubTopic();
    unsubPush();
  };
}
