// ---- Topic: a branded type-safe key ----

/**
 * A typed topic key. The phantom `__brand` field captures `T` so that
 * TypeScript's structural type system can distinguish Topic<A> from
 * Topic<B>. It is never populated at runtime.
 */
export interface Topic<T> {
  readonly name: string;
  /** @internal phantom field — do not use at runtime */
  readonly __brand: T;
}

/**
 * Create a topic singleton. The `as` cast is safe because `__brand`
 * is a phantom field that only exists for the type checker.
 */
export function topic<T>(name: string): Topic<T> {
  return { name } as Topic<T>;
}

// ---- Ksp interface ----

/**
 * Core telemetry interface. Implementations manage the data source
 * (websocket, simulation, replay, etc.) and dispatch frames to
 * subscribers. Framework-agnostic — the Svelte bindings layer wraps
 * this into runes.
 */
export interface Ksp {
  /**
   * Subscribe to a topic. Returns an unsubscribe function.
   * Ref-counted: the first subscriber activates the topic,
   * the last unsubscribe deactivates it.
   */
  subscribe<T>(topic: Topic<T>, cb: (frame: T) => void): () => void;

  /** Start the data source. Resolves when ready to receive subscriptions. */
  connect(): Promise<void>;

  /** Tear down all state, listeners, and connections. */
  destroy(): void;
}
