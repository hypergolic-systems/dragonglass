import type { Capability } from './capabilities';

/**
 * Operations invocable on the `game` topic. Fire-and-forget, no ack.
 *
 * `setCapabilities` is the UI's startup handshake: it tells the plugin
 * which stock KSP UI slices this app replaces. Unknown capability
 * strings are logged and ignored on the plugin side, so newer UIs can
 * declare caps an older plugin build doesn't understand.
 */
export interface GameOps {
  setCapabilities(capabilities: readonly Capability[]): void;
}
