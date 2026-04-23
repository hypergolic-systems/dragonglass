// Inbound op dispatcher.
//
// Client → mod wire format:
//   {"topic":"<name>","op":"<method>","args":[...primitives...]}
//
// Fire-and-forget — no response, no id. Parse errors and unknown
// topics/ops are logged + discarded; the transport stays up.
//
// Threading. `WebSocketServer.TextReceived` fires on the connection's
// reader thread. We parse there (cheap + isolated from KSP state) and
// enqueue the result on a `ConcurrentQueue`. `Drain()` runs on Unity's
// main thread from `TelemetryAddon.Update`, where handlers may freely
// touch FlightGlobals / Vessel / action groups.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Dragonglass.Telemetry.Util;
using Dragonglass.Telemetry.WebSocket;
using UnityEngine;

namespace Dragonglass.Telemetry.Topics
{
    internal sealed class OpDispatcher
    {
        private const string LogPrefix = "[Dragonglass/Telemetry] ";

        private readonly TopicRegistry _registry;
        private readonly ConcurrentQueue<Envelope> _queue = new ConcurrentQueue<Envelope>();

        private struct Envelope
        {
            public string Topic;
            public string Op;
            public List<object> Args;
        }

        public OpDispatcher(TopicRegistry registry, WebSocketServer server)
        {
            _registry = registry;
            server.TextReceived += OnText;
        }

        // Reader thread.
        private void OnText(WebSocketConnection conn, string text)
        {
            if (!TryParseEnvelope(text, out Envelope env))
            {
                Debug.LogWarning(LogPrefix + "dropping malformed op frame from " +
                                 conn.Remote + ": " + Truncate(text, 120));
                return;
            }
            _queue.Enqueue(env);
        }

        // Main thread.
        public void Drain()
        {
            while (_queue.TryDequeue(out Envelope env))
            {
                // Transport-level reserved ops. `subscribe` /
                // `unsubscribe` aren't routed to any Topic — they're
                // signals driven by the client's first-subscriber /
                // last-subscriber transitions on the named topic, so
                // fan them out through SubscriptionBus for dedicated
                // listeners (PartSubscriptionManager, etc.) to handle.
                if (env.Op == "subscribe")
                {
                    SubscriptionBus.RaiseSubscribe(env.Topic);
                    continue;
                }
                if (env.Op == "unsubscribe")
                {
                    SubscriptionBus.RaiseUnsubscribe(env.Topic);
                    continue;
                }
                Topic topic = _registry.GetByName(env.Topic);
                if (topic == null)
                {
                    Debug.LogWarning(LogPrefix + "unknown topic '" + env.Topic + "'");
                    continue;
                }
                try
                {
                    topic.HandleOp(env.Op, env.Args);
                }
                catch (Exception e)
                {
                    Debug.LogWarning(LogPrefix + "HandleOp " + env.Topic + "/" +
                                     env.Op + " threw: " + e);
                }
            }
        }

        private static bool TryParseEnvelope(string text, out Envelope env)
        {
            env = default;
            if (!(Json.Parse(text) is Dictionary<string, object> root)) return false;
            if (!(root.TryGetValue("topic", out object t) && t is string topic)) return false;
            if (!(root.TryGetValue("op", out object o) && o is string op)) return false;
            // `args` is optional; absent == empty.
            List<object> args = null;
            if (root.TryGetValue("args", out object a))
            {
                args = a as List<object>;
                if (args == null) return false;
            }
            env.Topic = topic;
            env.Op = op;
            env.Args = args ?? new List<object>();
            return true;
        }

        private static string Truncate(string s, int max)
        {
            if (s == null) return "<null>";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
