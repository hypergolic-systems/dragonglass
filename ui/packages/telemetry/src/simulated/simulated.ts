import type { Topic, Ksp, OpArgs } from '../core/ksp';
import { FlightTopic, GameTopic, AssemblyTopic } from '../core/topics';
import type { GameData } from '../core/game-data';
import { FlightSimulation } from './flight-sim';
import { ASSEMBLY } from './assembly-fixture';

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
  private cleanupKeyboard: (() => void) | null = null;

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
    this.startLoop();
    return Promise.resolve();
  }

  destroy(): void {
    cancelAnimationFrame(this.raf);
    this.cleanupKeyboard?.();
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
      if (['w', 'a', 's', 'd', 'q', 'e', 't'].includes(k)) {
        e.preventDefault();
      }
      if (k === 't') {
        sim.resetPending = true;
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

      this.sim.tick(dt);
      this.dispatch(FlightTopic, this.sim.data);

      this.raf = requestAnimationFrame(tick);
    };

    this.raf = requestAnimationFrame(tick);
  }
}
