import type { Topic, Ksp, OpArgs } from '../core/ksp';
import {
  FlightTopic,
  GameTopic,
  AssemblyTopic,
  EngineTopic,
  PawTopic,
  PartTopic,
  StageTopic,
  PartCatalogTopic,
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
import { SIM_STAGE_DATA, SIM_STAGE_DATA_EDITOR } from './stages-fixture';
import { SIM_PART_CATALOG } from './catalog-fixture';
import {
  ENGINES_SIM,
  CLUSTER_DRAIN_SECONDS,
  ENGINES_CYCLE_SECONDS,
} from './engines-fixture';
import { SIMULATED_PARTS, type SimulatedPart } from './parts-fixture';

const PART_TOPIC_PREFIX = 'part/';

// Simulated game state: starts in FLIGHT so `just ui-dev` lands on
// the flight UI without needing KSP running. F2 toggles to EDITOR
// so the editor PAW path is reachable from the browser — the same
// right-click-opens-a-PAW flow applies.
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
    } else if (topic.name === StageTopic.name) {
      const stage = SIM_GAME.scene === 'EDITOR' ? SIM_STAGE_DATA_EDITOR : SIM_STAGE_DATA;
      (cb as (frame: any) => void)(stage);
    } else if (topic.name === PartCatalogTopic.name) {
      (cb as (frame: any) => void)(SIM_PART_CATALOG);
    } else if (topic.name === EngineTopic.name) {
      (cb as (frame: any) => void)(this.lastEngines);
    } else if (topic.name.startsWith(PART_TOPIC_PREFIX)) {
      // PartTopic(id) — prime the cache so the first subscriber on a
      // never-before-seen part gets a frame this tick rather than
      // waiting for the 10 Hz loop to come around. Editor skips the
      // drain model so fixture loadouts stay at 100%.
      const partId = topic.name.slice(PART_TOPIC_PREFIX.length);
      const drained = SIM_GAME.scene !== 'EDITOR';
      const cached = this.lastParts.get(partId) ?? buildPart(partId, this.elapsed, drained);
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
      return;
    }

    // Editor resource tweaking. Only honour in EDITOR so the
    // simulator mirrors the server's scene gate — keeps demos honest.
    if (topic.name.startsWith(PART_TOPIC_PREFIX)
        && op === 'setResource'
        && SIM_GAME.scene === 'EDITOR'
        && typeof args[0] === 'string'
        && typeof args[1] === 'number') {
      const partId = topic.name.slice(PART_TOPIC_PREFIX.length);
      const prev = this.lastParts.get(partId);
      if (!prev) return;
      const name = args[0];
      const amount = args[1];
      const resources = prev.resources.map((r) =>
        r.name === name
          ? { ...r, available: Math.max(0, Math.min(r.capacity, amount)) }
          : r,
      );
      const next: PartData = { ...prev, resources };
      this.lastParts.set(partId, next);
      this.dispatch(PartTopic(partId), next);
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

  private dispatch<T, Ops>(topic: Topic<T, Ops>, data: T): void {
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
      // F2 flips the simulated scene between FLIGHT and EDITOR so the
      // editor PAW + staging paths are exercisable without a KSP
      // install. StageTopic also re-emits so the staging stack picks
      // up the editor-flavoured fixture (currentStageIdx = -1).
      if (k === 'f2') {
        e.preventDefault();
        SIM_GAME.scene = SIM_GAME.scene === 'FLIGHT' ? 'EDITOR' : 'FLIGHT';
        this.dispatch(GameTopic, SIM_GAME);
        this.dispatch(
          StageTopic,
          SIM_GAME.scene === 'EDITOR' ? SIM_STAGE_DATA_EDITOR : SIM_STAGE_DATA,
        );
        return;
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
      //
      // In EDITOR the simulated drain is suppressed — stock fuel tanks
      // don't leak on the assembly floor, and we need the
      // `setResource` writes to stick across ticks rather than being
      // clobbered by the drain model every frame.
      const editor = SIM_GAME.scene === 'EDITOR';
      for (const name of this.subs.keys()) {
        if (!name.startsWith(PART_TOPIC_PREFIX)) continue;
        const partId = name.slice(PART_TOPIC_PREFIX.length);
        const built = buildPart(partId, this.elapsed, !editor);
        const prev = this.lastParts.get(partId);
        const frame: PartData = editor && prev
          ? { ...built, resources: prev.resources }
          : built;
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

function buildPart(persistentId: string, elapsed: number, drain = true): PartData {
  const fixture = SIMULATED_PARTS.find((p) => p.persistentId === persistentId);
  if (!fixture) {
    return {
      persistentId,
      name: `UNKNOWN PART ${persistentId}`,
      screen: null,
      resources: [],
      modules: [],
    };
  }
  // `drain=false` (editor mode) keeps the fixture's declared amount /
  // capacity / flow verbatim — stock VAB resources are frozen until
  // the player tunes them, so the simulator mirrors that.
  const resources = drain
    ? fixture.resources.map((r) => scaleResource(r, elapsed))
    : fixture.resources.map((r) => ({ ...r, flow: undefined }));
  return {
    persistentId,
    name: fixture.name,
    screen: screenPosFor(fixture, elapsed),
    resources,
    modules: fixture.modules,
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
