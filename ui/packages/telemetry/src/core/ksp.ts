// ---- Topic: a branded type-safe key ----

/**
 * A typed topic key. The phantom `__brand` captures the inbound data
 * type `T`; the phantom `__opsBrand` captures an optional ops
 * interface (methods the client can invoke on the server). Both are
 * phantom — never populated at runtime — and `Ops` defaults to
 * `never`, so read-only topics stay unchanged.
 */
export interface Topic<T, Ops = never> {
  readonly name: string;
  /** @internal phantom field — do not use at runtime */
  readonly __brand: T;
  /** @internal phantom field — do not use at runtime */
  readonly __opsBrand: Ops;
}

/**
 * Create a topic singleton. The `as` cast is safe because both
 * phantom fields only exist for the type checker.
 */
export function topic<T, Ops = never>(name: string): Topic<T, Ops> {
  return { name } as Topic<T, Ops>;
}

/** Extract the parameter tuple of a method on an ops interface. */
export type OpArgs<Ops, K extends keyof Ops> =
  Ops[K] extends (...args: infer A) => unknown ? A : never;

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
   *
   * `tObserved` is the local-clock time (seconds, same units as
   * `performance.now() / 1000`) at which the frame was observed. For
   * live frames this is arrival time; for cached snapshot frames
   * replayed to late-mounting subscribers it's the replay time, so
   * downstream interpolators treat the value as "the latest known
   * state, current as of now" rather than projecting forward by
   * however long ago the topic last changed.
   *
   * Today this is derived from `performance.now()`. In the future,
   * once we estimate the server↔client clock offset (NTP-style), the
   * transport will instead derive it from the envelope's `t_server`
   * field minus the estimated offset, removing network-jitter from
   * the observed timestamp. The seam lives in the transport;
   * subscribers don't have to change.
   */
  subscribe<T, Ops>(
    topic: Topic<T, Ops>,
    cb: (frame: T, tObserved: number) => void,
  ): () => void;

  /**
   * Invoke an op on a topic (client → server). Fire-and-forget —
   * no ack, no reply. Only usable against topics whose `Ops`
   * parameter is populated; the op name and argument tuple are
   * both type-checked against that interface.
   */
  send<T, Ops, K extends keyof Ops & string>(
    topic: Topic<T, Ops>,
    op: K,
    ...args: OpArgs<Ops, K>
  ): void;

  /** Start the data source. Resolves when ready to receive subscriptions. */
  connect(): Promise<void>;

  /** Tear down all state, listeners, and connections. */
  destroy(): void;
}
