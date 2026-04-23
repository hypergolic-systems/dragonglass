// Static fan-out for transport-level subscribe/unsubscribe signals.
//
// The client's transport (DragonglassTelemetry) translates
// first-subscriber / last-subscriber transitions into reserved
// `{"op":"subscribe"|"unsubscribe","topic":"<name>"}` envelopes.
// OpDispatcher intercepts those and raises the corresponding event
// here; listeners (today: PartSubscriptionManager for `part/*`)
// choose which prefixes they handle.
//
// Matches the PawBus pattern: a tiny static bus in the Telemetry
// assembly so patch code (and, in this case, OpDispatcher which lives
// in the same assembly) can fire events without compile-time
// knowledge of the subscribers.

using System;

namespace Dragonglass.Telemetry
{
    public static class SubscriptionBus
    {
        /// <summary>
        /// Raised when the client signals it wants frames for
        /// <paramref name="topicName"/>. Called on Unity's main thread
        /// by OpDispatcher.Drain, so subscribers may AddComponent
        /// freely. Always-on topics (flight, engines, ...) are
        /// typically handled by no-op listeners.
        /// </summary>
        public static event Action<string> SubscribeRequested;

        /// <summary>
        /// Raised when the client drops its last subscriber for
        /// <paramref name="topicName"/>.
        /// </summary>
        public static event Action<string> UnsubscribeRequested;

        internal static void RaiseSubscribe(string topicName)
        {
            Action<string> h = SubscribeRequested;
            if (h != null) h(topicName);
        }

        internal static void RaiseUnsubscribe(string topicName)
        {
            Action<string> h = UnsubscribeRequested;
            if (h != null) h(topicName);
        }
    }
}
