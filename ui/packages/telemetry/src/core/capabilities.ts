/**
 * Capabilities a UI declares at startup via `GameTopic.setCapabilities`.
 * Each string names a slice of stock KSP chrome that the UI replaces;
 * the plugin suppresses only what's declared, so a UI that doesn't
 * replace (say) the editor parts panel can leave stock's in place.
 *
 * With no capabilities declared, the plugin leaves all stock UI intact.
 */

export const CAP_FLIGHT_UI      = 'flight/ui' as const;
export const CAP_FLIGHT_PAW     = 'flight/paw' as const;
export const CAP_EDITOR_PARTS   = 'editor/parts' as const;
export const CAP_EDITOR_PAW     = 'editor/paw' as const;
export const CAP_EDITOR_STAGING = 'editor/staging' as const;

export type Capability =
  | typeof CAP_FLIGHT_UI
  | typeof CAP_FLIGHT_PAW
  | typeof CAP_EDITOR_PARTS
  | typeof CAP_EDITOR_PAW
  | typeof CAP_EDITOR_STAGING;
