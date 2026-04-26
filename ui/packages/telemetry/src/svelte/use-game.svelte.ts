import { getKsp } from './context';
import { GameTopic } from '../core/topics';
import { decodeGame } from '../dragonglass/decoders';
import type { GameData } from '../core/game-data';

function defaults(): GameData {
  return { scene: '', activeVesselId: null, timewarp: 1, mapActive: false };
}

/**
 * Subscribe to the `game` topic. Returns a reactive proxy so components
 * can read `game.scene` / `game.timewarp` / `game.activeVesselId` with
 * fine-grained reactivity.
 *
 * Must be called during component initialization (needs Svelte context).
 */
export function useGame(): GameData {
  const telemetry = getKsp();
  let data = $state<GameData>(defaults());

  $effect(() => {
    return telemetry.subscribe(GameTopic, (raw) => {
      Object.assign(data, decodeGame(raw));
    });
  });

  return data;
}
