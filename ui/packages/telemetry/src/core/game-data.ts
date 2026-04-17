/**
 * Discrete game state: which scene KSP is in, the active vessel's ID,
 * and the current timewarp multiplier.
 *
 * `scene` is the `GameScenes` enum name serialised as a string — e.g.
 * "FLIGHT", "MAINMENU", "SPACECENTER", "EDITOR", "TRACKSTATION".
 * Consumers compare against literals; mirroring KSP's enum in TS
 * invites drift.
 *
 * `timewarp` of 1-4 is physics warp; 5+ is on-rails (5, 10, 50, 100,
 * 1000, ...).
 */
export interface GameData {
  scene: string;
  activeVesselId: string | null;
  timewarp: number;
}
