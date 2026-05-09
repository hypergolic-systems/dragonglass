// UI-declared capability set.
//
// At startup a connected UI sends GameTopic.setCapabilities([...])
// announcing which stock KSP UI elements it replaces. Every Harmony
// patch that hides or intercepts stock UI consults this set before
// acting; with no caps declared (no UI connected, or UI declined all
// of them), stock KSP runs untouched.
//
// Threading. setCapabilities arrives via OpDispatcher.Drain on Unity's
// main thread, and every reader runs from a Unity lifecycle callback
// (Harmony postfix on Start/Awake, PAW prefix on right-click). Reads
// and the single writer therefore all happen on the main thread — no
// lock required.
//
// Known caps. Unknown strings are dropped with an info log so the wire
// stays forward-compatible: a newer UI can declare caps this build
// doesn't understand without breaking the handshake.

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Dragonglass.Telemetry
{
    public static class Capabilities
    {
        public const string FlightUi      = "flight/ui";
        public const string FlightPaw     = "flight/paw";
        public const string EditorParts   = "editor/parts";
        public const string EditorPaw     = "editor/paw";
        public const string EditorStaging = "editor/staging";
        public const string EditorToolbar = "editor/toolbar";

        private static readonly HashSet<string> Known = new HashSet<string>
        {
            FlightUi, FlightPaw, EditorParts, EditorPaw, EditorStaging, EditorToolbar,
        };

        private static HashSet<string> _current = new HashSet<string>();

        public static bool Has(string cap) => _current.Contains(cap);

        public static void Set(IEnumerable<string> caps)
        {
            var next = new HashSet<string>();
            foreach (var c in caps)
            {
                if (c == null) continue;
                if (Known.Contains(c)) next.Add(c);
                else Debug.Log("[Dragonglass/Telemetry] ignoring unknown capability '" + c + "'");
            }
            _current = next;
            Debug.Log("[Dragonglass/Telemetry] capabilities = [" + string.Join(",", _current.ToArray()) + "]");
        }
    }
}
