import type { Topic, Ksp, OpArgs } from '../core/ksp';
import {
  FlightTopic,
  GameTopic,
  AssemblyTopic,
  EngineTopic,
  PawTopic,
} from '../core/topics';
import type { GameData } from '../core/game-data';
import type {
  EngineData,
  EnginePoint,
  EnginePropellant,
  EngineStatus,
} from '../core/engine-data';
import type { PartData, PartResourceData } from '../core/part-data';
import { FlightSimulation } from './flight-sim';
import { ASSEMBLY } from './assembly-fixture';
import {
  ENGINES_SIM,
  CLUSTER_DRAIN_SECONDS,
  ENGINES_CYCLE_SECONDS,
} from './engines-fixture';
import { SIMULATED_PARTS, type SimulatedPart } from './parts-fixture';

const PART_TOPIC_PREFIX = 'part/';

// Simulated game state: pin to FLIGHT so `just ui-dev` lands on the
// flight UI without needing KSP running.
const SIM_GAME: GameData = {
  scene: 'FLIGHT',
  activeVesselId: 'sim-vessel',
  timewarp: 1,
};

export class SimulatedKsp implements Ksp {
  private subs = new Map<string, Set<(frame: any) => void>>();
  private sim = new FlightSimulation();
  private raf = 0;
  private last = 0;
  private elapsed = 0;
  private cleanupKeyboard: (() => void) | null = null;
  private cleanupPawClicks: (() => void) | null = null;

  // Last emitted engine frame — used as the snapshot for late
  // subscribers. No defensive cloning; frames are immutable per
  // their `readonly` types, so handing out the same reference is
  // safe.
  private lastEngines: EngineData = buildEngines(0);

  // Per-part frame cache. Populated on first subscription for a given
  // partId; retained across (un)subscribes so returning subscribers get
  // the most recent frame immediately. Map<persistentId, PartData>.
  private lastParts = new Map<string, PartData>();

  subscribe<T, Ops>(topic: Topic<T, Ops>, cb: (frame: T) => void): () => void {
    let set = this.subs.get(topic.name);
    if (!set) {
      set = new Set();
      this.subs.set(topic.name, set);
    }
    set.add(cb as (frame: any) => void);

    // Fire immediately for topics with a fixed simulated value so
    // late-mounting components don't need to wait for a tick.
    if (topic.name === AssemblyTopic.name) {
      (cb as (frame: any) => void)(ASSEMBLY);
    } else if (topic.name === GameTopic.name) {
      (cb as (frame: any) => void)(SIM_GAME);
    } else if (topic.name === EngineTopic.name) {
      (cb as (frame: any) => void)(this.lastEngines);
    } else if (topic.name.startsWith(PART_TOPIC_PREFIX)) {
      // PartTopic(id) — prime the cache so the first subscriber on a
      // never-before-seen part gets a frame this tick rather than
      // waiting for the 10 Hz loop to come around.
      const partId = topic.name.slice(PART_TOPIC_PREFIX.length);
      const cached = this.lastParts.get(partId) ?? buildPart(partId, this.elapsed);
      this.lastParts.set(partId, cached);
      (cb as (frame: any) => void)(cached);
    }

    return () => {
      set!.delete(cb as (frame: any) => void);
      if (set!.size === 0) this.subs.delete(topic.name);
    };
  }

  send<T, Ops, K extends keyof Ops & string>(
    topic: Topic<T, Ops>,
    op: K,
    ...args: OpArgs<Ops, K>
  ): void {
    // Forward flight ops to the local simulation so dev-mode buttons
    // are actually interactive. Unknown (topic, op) combinations drop
    // silently — same posture as the mod.
    if (topic.name === FlightTopic.name) {
      if (op === 'setSas' && typeof args[0] === 'boolean') {
        this.sim.setSas(args[0]);
      } else if (op === 'setRcs' && typeof args[0] === 'boolean') {
        this.sim.setRcs(args[0]);
      }
    }
  }

  connect(): Promise<void> {
    this.startKeyboard();
    this.startPawClicks();
    this.startLoop();
    return Promise.resolve();
  }

  destroy(): void {
    cancelAnimationFrame(this.raf);
    this.cleanupKeyboard?.();
    this.cleanupPawClicks?.();
    this.subs.clear();
  }

  private dispatch<T>(topic: Topic<T>, data: T): void {
    const set = this.subs.get(topic.name);
    if (set) {
      for (const cb of set) cb(data);
    }
  }

  private startKeyboard(): void {
    const { sim } = this;

    const down = (e: KeyboardEvent) => {
      const k = e.key.toLowerCase();
      if (['w', 'a', 's', 'd', 'q', 'e', 't', 'm'].includes(k)) {
        e.preventDefault();
      }
      if (k === 't') {
        sim.resetPending = true;
        return;
      }
      if (k === 'm') {
        sim.cycleSpeedMode();
        return;
      }
      sim.keys.add(k);
    };

    const up = (e: KeyboardEvent) => {
      sim.keys.delete(e.key.toLowerCase());
    };

    const blur = () => sim.keys.clear();

    window.addEventListener('keydown', down);
    window.addEventListener('keyup', up);
    window.addEventListener('blur', blur);

    this.cleanupKeyboard = () => {
      window.removeEventListener('keydown', down);
      window.removeEventListener('keyup', up);
      window.removeEventListener('blur', blur);
    };
  }

  private startLoop(): void {
    this.last = performance.now();

    const tick = (now: number) => {
      const dt = Math.min(0.05, (now - this.last) / 1000);
      this.last = now;
      this.elapsed += dt;

      const flight = this.sim.tick(dt);
      const engines = buildEngines(this.elapsed);
      this.lastEngines = engines;

      this.dispatch(FlightTopic, flight);
      this.dispatch(EngineTopic, engines);

      // Re-emit part frames only for parts that have a live subscriber
      // this tick. The positional Lissajous drifts slowly, so even
      // idle PAWs see visibly moving leader lines.
      for (const name of this.subs.keys()) {
        if (!name.startsWith(PART_TOPIC_PREFIX)) continue;
        const partId = name.slice(PART_TOPIC_PREFIX.length);
        const frame = buildPart(partId, this.elapsed);
        this.lastParts.set(partId, frame);
        const bucket = this.subs.get(name);
        if (bucket) for (const cb of bucket) cb(frame);
      }

      this.raf = requestAnimationFrame(tick);
    };

    this.raf = requestAnimationFrame(tick);
  }

  // Dev-mode PAW trigger. Right-click anywhere in the document fires a
  // PawTopic event carrying the persistentId of whichever simulated
  // part sits closest to the cursor in viewport space. The `contextmenu`
  // listener App.svelte installs already cancels the native menu, so
  // this piggybacks on the same event without fighting it.
  private startPawClicks(): void {
    const onContext = (e: MouseEvent) => {
      const partId = nearestPartToCursor(e.clientX, e.clientY, this.elapsed);
      if (partId === null) return;
      this.dispatch(PawTopic, { persistentId: partId });
    };
    window.addEventListener('contextmenu', onContext, true);
    this.cleanupPawClicks = () => {
      window.removeEventListener('contextmenu', onContext, true);
    };
  }
}

// ---- Part-frame builder -----------------------------------------
//
// Maps a simulated part into a live PartData frame: position orbits a
// slow Lissajous around the viewport centre; resources drain
// deterministically from `elapsed` so subscriber reconnects land on
// the same state they would have seen without interruption.

const DRAIN_CYCLE_SECONDS = 180;

function buildPart(persistentId: string, elapsed: number): PartData {
  const fixture = SIMULATED_PARTS.find((p) => p.persistentId === persistentId);
  if (!fixture) {
    return {
      persistentId,
      name: `UNKNOWN PART ${persistentId}`,
      screen: null,
      resources: [],
    };
  }
  return {
    persistentId,
    name: fixture.name,
    screen: screenPosFor(fixture, elapsed),
    resources: fixture.resources.map((r) => scaleResource(r, elapsed)),
  };
}

function screenPosFor(p: SimulatedPart, elapsed: number): PartData['screen'] {
  const w = typeof window === 'undefined' ? 1920 : window.innerWidth;
  const h = typeof window === 'undefined' ? 1080 : window.innerHeight;
  const cx = w / 2;
  const cy = h / 2;
  // Base in [-1, 1] of the short dimension; lissajous on top.
  const r = Math.min(w, h) * 0.45;
  const t = elapsed + p.phase;
  const x = cx + (p.baseX + p.ampX * Math.sin(t * 0.31)) * r;
  const y = cy + (p.baseY + p.ampY * Math.cos(t * 0.43)) * r;
  return { x, y, visible: true };
}

function scaleResource(r: PartResourceData, elapsed: number): PartResourceData {
  // Drain from capacity to zero over DRAIN_CYCLE, then refill — so
  // thresholds (warn/alert) visibly cycle without the UI ever
  // saturating empty for long.
  if (r.capacity <= 0) return r;
  const t = (elapsed % DRAIN_CYCLE_SECONDS) / DRAIN_CYCLE_SECONDS;
  const phase = t < 0.85 ? 1 - t / 0.85 : (t - 0.85) / 0.15;
  const available = Math.max(0, Math.min(r.capacity, r.capacity * phase));
  return { ...r, available };
}

// Nearest simulated part to a viewport coordinate. Used by the dev
// right-click hook so a right-click opens a plausibly-associated PAW.
function nearestPartToCursor(
  x: number,
  y: number,
  elapsed: number,
): string | null {
  let best: string | null = null;
  let bestDist = Infinity;
  for (const fixture of SIMULATED_PARTS) {
    const screen = screenPosFor(fixture, elapsed);
    if (!screen) continue;
    const dx = screen.x - x;
    const dy = screen.y - y;
    const d = dx * dx + dy * dy;
    if (d < bestDist) {
      bestDist = d;
      best = fixture.persistentId;
    }
  }
  return best;
}

// ---- Pure frame builders ------------------------------------
//
// Derives the engine frame entirely from elapsed time — no hidden
// mutable state, no clones of the fixture. Each call produces a
// fresh, deeply-immutable object whose reference changes every tick
// so Svelte `$state` stores detect the update.

function buildEngines(elapsed: number): EngineData {
  const cycleT = elapsed % ENGINES_CYCLE_SECONDS;
  const engines: EnginePoint[] = ENGINES_SIM.map((e) => {
    const drain = CLUSTER_DRAIN_SECONDS[e.clusterId] ?? ENGINES_CYCLE_SECONDS;
    const frac = Math.max(0, 1 - cycleT / drain);
    const propellants: EnginePropellant[] = e.propellants.map((p) => ({
      resourceName: p.resourceName,
      abbr: p.abbr,
      available: p.capacity * frac,
      capacity: p.capacity,
    }));
    const status: EngineStatus = frac > 0 ? 'burning' : 'flameout';
    return {
      id: e.id,
      x: e.x,
      y: e.y,
      status,
      maxThrust: e.maxThrust,
      isp: e.isp,
      crossfeedPartIds: e.crossfeedPartIds,
      propellants,
    };
  });
  return {
    vesselId: 'sim-vessel',
    engines,
  };
}
