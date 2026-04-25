/**
 * Discrete game state: which scene KSP is in, the active vessel's ID,
 * the current timewarp multiplier, and whether the flight scene is in
 * map view.
 *
 * `scene` is the `GameScenes` enum name serialised as a string — e.g.
 * "FLIGHT", "MAINMENU", "SPACECENTER", "EDITOR", "TRACKSTATION".
 * Consumers compare against literals; mirroring KSP's enum in TS
 * invites drift.
 *
 * `timewarp` of 1-4 is physics warp; 5+ is on-rails (5, 10, 50, 100,
 * 1000, ...).
 *
 * `mapActive` is `true` while flight map view is open (M key /
 * map button). The flight HUD uses it to hide screen-space chrome
 * (PAWs, tape annotations) whose anchors don't make sense once the
 * world projection is replaced by the orbital map.
 */
export interface GameData {
  scene: string;
  activeVesselId: string | null;
  timewarp: number;
  mapActive: boolean;
}
