// Static event-bus that PartTopic uses to publish "this part's
// GameObject is being destroyed" tombstones. TopicBroadcaster listens
// and writes a final empty-payload frame on the per-part wire so the
// UI can close the matching PAW.
//
// Design: PartTopic emits the tombstone on its own channel
// (`part/<id>`) instead of carrying it on a separate `partGone`
// topic. One channel per part means subscribers receive both state
// updates and termination signals through the same callback —
// consumers don't have to keep a second subscription alive just to
// learn when the topic dies.
//
// Sister to PawBus, same pattern: the topic raises, the broadcaster
// handles. Lives in the Telemetry assembly so PartTopic can reach it
// directly.

using System;

namespace Dragonglass.Telemetry
{
    public static class PartGoneBus
    {
        /// <summary>
        /// Raised when a PartTopic detects its sibling Part has been
        /// Unity-nulled (the GameObject is being destroyed). Carries
        /// the wire topic name (e.g. <c>"part/123456"</c>) so the
        /// broadcaster can send the tombstone envelope directly
        /// without re-deriving it.
        /// </summary>
        public static event Action<string> PartTopicDying;

        public static void Raise(string topicName)
        {
            Action<string> handler = PartTopicDying;
            if (handler != null) handler(topicName);
        }
    }
}
