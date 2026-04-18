/**
 * Operations that can be invoked on the active vessel via the
 * `flight` topic. Each method is fire-and-forget on the wire —
 * there is no return value and no ack. The server applies the
 * effect (action-group toggle, throttle set, …) on its next
 * Unity frame and the resulting state shows up in the next
 * FlightTopic broadcast.
 */
export interface FlightOps {
  setSas(enabled: boolean): void;
  setRcs(enabled: boolean): void;
}
