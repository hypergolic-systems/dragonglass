// Bridges the TopicRegistry and the WebSocketServer.
//
// On a fixed cadence (10 Hz by default), iterates live topics and
// broadcasts the ones that have flipped dirty since the last pass.
// Caches the most recent broadcast frame per topic so that a client
// connecting mid-stream immediately gets a snapshot of every topic's
// current state without waiting for the next change.

using System.Collections.Generic;
using System.Text;
using Dragonglass.Telemetry.Util;
using Dragonglass.Telemetry.WebSocket;
using UnityEngine;

namespace Dragonglass.Telemetry.Topics
{
    internal sealed class TopicBroadcaster
    {
        private const float FlushIntervalSec = 0.1f;  // 10 Hz

        private readonly TopicRegistry _registry;
        private readonly WebSocketServer _server;

        // Last broadcast frame per topic name. Replayed to new clients.
        private readonly Dictionary<string, string> _lastByTopic =
            new Dictionary<string, string>();

        // Scratch buffer reused across flushes to avoid allocation churn.
        private readonly StringBuilder _sb = new StringBuilder(256);

        private float _nextFlush;

        public TopicBroadcaster(TopicRegistry registry, WebSocketServer server)
        {
            _registry = registry;
            _server = server;
            _server.ClientConnected += OnClientConnected;
        }

        /// <summary>
        /// Called from <c>TelemetryAddon.Update</c>. Rate-limited
        /// internally to <see cref="FlushIntervalSec"/>; cheap to
        /// invoke every frame.
        /// </summary>
        public void Tick()
        {
            if (Time.realtimeSinceStartup < _nextFlush) return;
            _nextFlush = Time.realtimeSinceStartup + FlushIntervalSec;

            IReadOnlyList<Topic> topics = _registry.All;
            for (int i = 0; i < topics.Count; i++)
            {
                Topic topic = topics[i];
                if (!topic.IsDirty) continue;

                string frame = BuildFrame(topic);
                // Event topics (e.g. PawTopic) are pulses, not state.
                // Replaying a cached pulse to a newly-connecting client
                // would double-fire the event minutes after the fact,
                // so skip the cache for them. See Topic.IsEvent.
                if (!topic.IsEvent) _lastByTopic[topic.Name] = frame;
                _server.Broadcast(frame);
                topic.ClearDirty();
            }
        }

        private string BuildFrame(Topic topic)
        {
            _sb.Length = 0;
            _sb.Append("{\"topic\":");
            Json.WriteString(_sb, topic.Name);
            _sb.Append(",\"data\":");
            topic.WriteData(_sb);
            _sb.Append('}');
            return _sb.ToString();
        }

        private void OnClientConnected(WebSocketConnection conn)
        {
            // Snapshot-on-connect: hand the new client the last
            // broadcast for every known topic so it doesn't have to
            // wait for the next change to learn current state.
            foreach (string frame in _lastByTopic.Values)
            {
                if (!conn.SendText(frame)) return;  // connection died mid-snapshot
            }
        }
    }
}
