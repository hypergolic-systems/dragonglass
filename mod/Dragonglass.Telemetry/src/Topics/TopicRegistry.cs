// Process-wide registry of live Topics, populated by Topic.OnEnable /
// OnDisable. TelemetryAddon sets the singleton instance at plugin
// start; it stays alive for the process lifetime.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Dragonglass.Telemetry.Topics
{
    public sealed class TopicRegistry
    {
        private const string LogPrefix = "[Dragonglass/Telemetry] ";

        public static TopicRegistry Instance { get; private set; }

        private readonly List<Topic> _topics = new List<Topic>();

        public IReadOnlyList<Topic> All { get { return _topics; } }

        /// <summary>
        /// Install <paramref name="instance"/> as the process-wide
        /// registry. Called once from <c>TelemetryAddon.Start</c>
        /// before any Topic components are added. Subsequent calls
        /// with the same instance are no-ops; with a different
        /// instance, logs and replaces.
        /// </summary>
        public static void SetInstance(TopicRegistry instance)
        {
            if (Instance == instance) return;
            if (Instance != null)
            {
                Debug.LogWarning(LogPrefix + "replacing existing TopicRegistry instance");
            }
            Instance = instance;
        }

        internal void Register(Topic topic)
        {
            if (topic == null) return;
            if (_topics.Contains(topic)) return;
            _topics.Add(topic);
        }

        internal void Unregister(Topic topic)
        {
            if (topic == null) return;
            _topics.Remove(topic);
        }

        /// <summary>
        /// Convenience accessor for a specific topic type. Returns
        /// null if no instance is registered.
        /// </summary>
        public T Get<T>() where T : Topic
        {
            for (int i = 0; i < _topics.Count; i++)
            {
                if (_topics[i] is T t) return t;
            }
            return null;
        }
    }
}
