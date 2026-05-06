// Runtime host detection.
//
// The same UI bundle runs in two environments:
//
//  1. The KSP-launched CEF sidecar, where the page is composited by
//     the Unity-side native plugin. The sidecar boots the page with
//     `?host=ksp` so the UI can opt into KSP-only behaviour (e.g.
//     painting the punch-through encoding row that the plugin reads
//     back from the IOSurface).
//
//  2. A vanilla browser — `npm run dev`, deep-link previews, design
//     review tabs. No compositor, no SHM, no KSP at all.
//
// Code that needs to gate behaviour on this should call `isHostKsp()`
// rather than sniffing `?ws=` or other coincidentally-present params.

const HOST_PARAM = 'host';
const HOST_KSP = 'ksp';

/** True when the page was launched by the KSP-side sidecar (and is
 *  therefore being composited by the Unity native plugin). False in
 *  the dev server or any vanilla browser tab. */
export function isHostKsp(): boolean {
  if (typeof window === 'undefined') return false;
  return new URLSearchParams(window.location.search).get(HOST_PARAM) === HOST_KSP;
}
