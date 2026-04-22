// Per-part telemetry for Part Action Windows (PAWs).
//
// `PartTopic(id)` is a parametrized topic: the name carries the KSP
// `persistentId` of the target part (`part/<id>`), so multiple PAWs
// coexist as independent subscriptions. The server emits a frame
// whenever the part's screen position moves or a resource tick crosses
// a dispatch threshold.
//
// `PawTopic` is an event-only topic — it carries the persistentId of
// whichever part the pilot right-clicked, with no stable value. The UI
// treats every dispatch as "open a PAW for this id".

export interface PartResourceData {
  /** KSP internal resource name, e.g. "LiquidFuel". */
  readonly name: string;
  /** Short label for the HUD, e.g. "LF" / "OX" / "EC". */
  readonly abbr: string;
  /** Units currently stored. */
  readonly available: number;
  /** Units of maximum capacity. */
  readonly capacity: number;
  /**
   * Optional rate of change, units/sec. Positive = inflow,
   * negative = drain. Omit for static resources; the UI will hide
   * the flow indicator.
   */
  readonly flow?: number;
}

export interface PartScreenPos {
  /** CSS pixels from the viewport left edge. */
  readonly x: number;
  /** CSS pixels from the viewport top edge. */
  readonly y: number;
  /** False when the part is behind the camera or off-screen. */
  readonly visible: boolean;
}

export interface PartData {
  readonly persistentId: string;
  /** Localized part title (e.g. "RT-10 'Hammer' Solid Fuel Booster"). */
  readonly name: string;
  /** Viewport-space projection of the part centre; null until first frame. */
  readonly screen: PartScreenPos | null;
  readonly resources: readonly PartResourceData[];
}

/**
 * PAW open event. No stable value — each dispatch is a pulse telling
 * the UI to open a window for `persistentId`. Handlers dedupe against
 * their current open-set; re-right-clicking an open part is a no-op.
 */
export interface PawEvent {
  readonly persistentId: string;
}
