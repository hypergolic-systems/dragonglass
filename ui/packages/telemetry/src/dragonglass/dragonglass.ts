// Live Ksp implementation backed by the Dragonglass KSP-side
// WebSocket broadcaster. Mirrors the Ksp interface shape of
// `SimulatedKsp`: topic-keyed subscription buckets, a single
// `connect()` that kicks off the transport, and `destroy()` for
// teardown.
//
// Transport: plain `WebSocket`, auto-reconnect on close with a flat
// 1 s delay (the server is local — exponential backoff is overkill).
// On each reconnect, the server's own snapshot-on-connect logic refills
// the last-known frame for every topic, so consumers converge without
// client-side caching of server state across disconnects.
//
// Snapshot-on-subscribe: a new subscriber added *after* a frame has
// already been dispatched gets the most recent cached frame
// immediately, so components that mount late don't stare at their
// defaults until the next server push.

import type { Ksp, Topic, OpArgs } from '../core/ksp';
import {
  decodeClock,
  decodeGame,
  decodeFlight,
  decodeEngines,
} from './decoders';

const RECONNECT_DELAY_MS = 1000;

type Decoder = (raw: unknown) => unknown;
type Callback = (frame: unknown) => void;

export class DragonglassTelemetry implements Ksp {
  private readonly url: string;
  private readonly subs = new Map<string, Set<Callback>>();
  private readonly decoders: Record<string, Decoder> = {
    clock: decodeClock,
    game: decodeGame,
    flight: decodeFlight,
    engines: decodeEngines,
  };
  private readonly lastByTopic = new Map<string, unknown>();

  private ws: WebSocket | null = null;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private destroyed = false;

  private connectPromise: Promise<void> | null = null;
  private connectResolve: (() => void) | null = null;

  constructor(url: string) {
    this.url = url;
  }

  subscribe<T, Ops>(topic: Topic<T, Ops>, cb: (frame: T) => void): () => void {
    let set = this.subs.get(topic.name);
    if (!set) {
      set = new Set();
      this.subs.set(topic.name, set);
    }
    set.add(cb as Callback);

    // If we've already received a frame for this topic, fire it
    // immediately so late-mounting consumers don't lag a tick behind.
    const last = this.lastByTopic.get(topic.name);
    if (last !== undefined) (cb as Callback)(last);

    return () => {
      const bucket = this.subs.get(topic.name);
      if (!bucket) return;
      bucket.delete(cb as Callback);
      if (bucket.size === 0) this.subs.delete(topic.name);
    };
  }

  send<T, Ops, K extends keyof Ops & string>(
    topic: Topic<T, Ops>,
    op: K,
    ...args: OpArgs<Ops, K>
  ): void {
    const ws = this.ws;
    if (!ws || ws.readyState !== WebSocket.OPEN) return;
    // Fire-and-forget. No queueing across reconnects — the next user
    // action will send once the socket is back up.
    try {
      ws.send(JSON.stringify({ topic: topic.name, op, args }));
    } catch (err) {
      console.warn('[dragonglass] send failed:', err);
    }
  }

  connect(): Promise<void> {
    if (this.connectPromise) return this.connectPromise;
    this.connectPromise = new Promise<void>((resolve) => {
      this.connectResolve = resolve;
    });
    this.openSocket();
    return this.connectPromise;
  }

  destroy(): void {
    this.destroyed = true;
    if (this.reconnectTimer !== null) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    if (this.ws) {
      this.ws.onopen = null;
      this.ws.onclose = null;
      this.ws.onerror = null;
      this.ws.onmessage = null;
      try { this.ws.close(); } catch {}
      this.ws = null;
    }
    this.subs.clear();
    this.lastByTopic.clear();
  }

  private openSocket(): void {
    if (this.destroyed) return;

    let ws: WebSocket;
    try {
      ws = new WebSocket(this.url);
    } catch (err) {
      console.warn('[dragonglass] websocket construct failed:', err);
      this.scheduleReconnect();
      return;
    }
    this.ws = ws;

    ws.onopen = () => {
      if (this.connectResolve) {
        this.connectResolve();
        this.connectResolve = null;
      }
    };

    ws.onmessage = (ev) => this.handleMessage(ev);

    ws.onerror = () => {
      // onclose fires right after; reconnect is driven from there.
    };

    ws.onclose = () => {
      if (this.ws === ws) this.ws = null;
      this.scheduleReconnect();
    };
  }

  private scheduleReconnect(): void {
    if (this.destroyed) return;
    if (this.reconnectTimer !== null) return;
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      this.openSocket();
    }, RECONNECT_DELAY_MS);
  }

  private handleMessage(ev: MessageEvent): void {
    if (typeof ev.data !== 'string') return;

    let parsed: { topic?: unknown; data?: unknown };
    try {
      parsed = JSON.parse(ev.data);
    } catch (err) {
      console.warn('[dragonglass] dropping non-JSON frame:', err);
      return;
    }

    const topic = parsed.topic;
    if (typeof topic !== 'string') return;

    const decode = this.decoders[topic];
    if (!decode) return;  // Unknown topic: forward-compat, drop silently.

    const frame = decode(parsed.data);
    this.lastByTopic.set(topic, frame);
    const bucket = this.subs.get(topic);
    if (bucket) {
      for (const cb of bucket) cb(frame);
    }
  }
}
