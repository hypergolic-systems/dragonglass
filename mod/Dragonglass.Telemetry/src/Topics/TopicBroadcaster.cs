// Bridges the TopicRegistry and the WebSocketServer.
//
// On a fixed cadence (10 Hz by default), iterates live topics and
// broadcasts the ones that have flipped dirty since the last pass.
// Caches the most recent payload per topic so that a client connecting
// mid-stream immediately gets a snapshot of every topic's current state
// without waiting for the next change.
//
// Every wire envelope carries a `t_server` field — the broadcaster's
// flush time in seconds (Unity `Time.realtimeSinceStartup`) — so the
// client can interpolate / extrapolate against a stable source clock
// rather than the jitter of WebSocket arrival times. Snapshot frames
// sent on client connect are re-stamped with the most recent flush
// time so the client treats them as "this is the latest known state"
// rather than projecting forward by however long ago the topic last
// changed.

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

        // Last broadcast payload (the JSON object that goes into the
        // envelope's `data` field) per topic name. Replayed to new
        // clients wrapped in a fresh envelope at connect time.
        private readonly Dictionary<string, string> _lastDataByTopic =
            new Dictionary<string, string>();

        // Most recent flush time, in seconds. Used to stamp snapshot
        // replay envelopes. `volatile` because OnClientConnected runs
        // on the accept thread.
        private volatile float _lastFlushTime;

        // Scratch buffer reused across flushes on the main thread.
        private readonly StringBuilder _sb = new StringBuilder(256);

        private float _nextFlush;

        public TopicBroadcaster(TopicRegistry registry, WebSocketServer server)
        {
            _registry = registry;
            _server = server;
            _server.ClientConnected += OnClientConnected;
            // Tombstones for per-part topics. PartTopic raises the
            // bus from its OnDestroy when the underlying Part
            // GameObject is being torn down (decoupled-and-unloaded,
            // exploded, editor-deleted) — by that point the topic is
            // already unregistered, so the normal IsDirty flush path
            // can't pick it up; we send a final empty-payload frame
            // directly. Drop the cached snapshot so a reconnecting
            // client doesn't replay the dead topic's last live frame.
            PartGoneBus.PartTopicDying += OnPartTopicDying;
        }

        /// <summary>
        /// Called from <c>TelemetryAddon.Update</c>. Rate-limited
        /// internally to <see cref="FlushIntervalSec"/>; cheap to
        /// invoke every frame.
        /// </summary>
        public void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextFlush) return;
            _nextFlush = now + FlushIntervalSec;
            _lastFlushTime = now;

            IReadOnlyList<Topic> topics = _registry.All;
            for (int i = 0; i < topics.Count; i++)
            {
                Topic topic = topics[i];
                if (!topic.IsDirty) continue;

                string dataJson = BuildData(topic);
                // Event topics (e.g. PawTopic) are pulses, not state.
                // Replaying a cached pulse to a newly-connecting client
                // would double-fire the event minutes after the fact,
                // so skip the cache for them. See Topic.IsEvent.
                if (!topic.IsEvent) _lastDataByTopic[topic.Name] = dataJson;
                string frame = BuildFrame(_sb, topic.Name, now, dataJson);
                _server.Broadcast(frame);
                topic.ClearDirty();
            }
        }

        private string BuildData(Topic topic)
        {
            _sb.Length = 0;
            topic.WriteData(_sb);
            return _sb.ToString();
        }

        private static string BuildFrame(StringBuilder sb, string name, float tServer, string dataJson)
        {
            sb.Length = 0;
            sb.Append("{\"topic\":");
            Json.WriteString(sb, name);
            sb.Append(",\"t_server\":");
            Json.WriteFloat(sb, tServer);
            sb.Append(",\"data\":");
            sb.Append(dataJson);
            sb.Append('}');
            return sb.ToString();
        }

        private void OnPartTopicDying(string topicName)
        {
            string frame = BuildFrame(_sb, topicName, _lastFlushTime, "[]");
            _server.Broadcast(frame);
            _lastDataByTopic.Remove(topicName);
        }

        private void OnClientConnected(WebSocketConnection conn)
        {
            // Snapshot-on-connect: hand the new client the last
            // payload for every known topic so it doesn't have to
            // wait for the next change to learn current state.
            //
            // Re-stamp with the most recent flush time so the client
            // treats the value as current. The alternative — sending
            // the original observation time — would make the client
            // extrapolate forward by however long ago the topic last
            // changed (potentially minutes), wildly diverging from
            // truth before the next real update lands.
            //
            // Runs on the accept thread; uses a local StringBuilder
            // to stay out of `_sb`, which the main thread owns.
            float t = _lastFlushTime;
            StringBuilder sb = new StringBuilder(256);
            foreach (KeyValuePair<string, string> kv in _lastDataByTopic)
            {
                string frame = BuildFrame(sb, kv.Key, t, kv.Value);
                if (!conn.SendText(frame)) return;  // connection died mid-snapshot
            }
        }
    }
}
