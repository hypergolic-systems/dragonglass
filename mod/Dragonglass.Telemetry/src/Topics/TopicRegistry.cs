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

        // Process-lifetime map of base topic types to override subclasses.
        // Mods (e.g. Nova) register here at startup so DG's installers
        // attach the override class instead of the stock one. Survives
        // scene transitions; cleared only on registry replacement.
        private readonly Dictionary<Type, Type> _typeOverrides = new Dictionary<Type, Type>();

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

        /// <summary>
        /// Look up a topic by its wire-level name. Used by the inbound
        /// op dispatcher to route `{"topic":"...","op":"..."}` frames
        /// to the right instance. Returns null if not found. Main-
        /// thread only (consistent with Register/Unregister).
        /// </summary>
        public Topic GetByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            for (int i = 0; i < _topics.Count; i++)
            {
                if (_topics[i].Name == name) return _topics[i];
            }
            return null;
        }

        /// <summary>
        /// Register <typeparamref name="TOverride"/> to be instantiated
        /// in place of <typeparamref name="TBase"/> wherever an installer
        /// resolves the topic class to attach. The generic constraint
        /// guarantees the override inherits the base's wire shape (Name
        /// and WriteData), so the schema can never drift — overrides
        /// only swap the data-collection step.
        /// <para>
        /// Process-lifetime; call once at startup before DG's installers
        /// run. Re-registering the same pair is idempotent; re-registering
        /// with a different override logs a warning and replaces.
        /// </para>
        /// </summary>
        public void RegisterOverride<TBase, TOverride>()
            where TBase : Topic
            where TOverride : TBase
        {
            var baseType = typeof(TBase);
            var overrideType = typeof(TOverride);
            if (_typeOverrides.TryGetValue(baseType, out var existing))
            {
                if (existing == overrideType) return;
                Debug.LogWarning(LogPrefix + "topic override conflict: "
                    + baseType.Name + " → " + existing.Name
                    + " replaced by " + overrideType.Name);
            }
            _typeOverrides[baseType] = overrideType;
        }

        /// <summary>
        /// Resolve the runtime type to instantiate for a given base
        /// topic, applying any registered override. Installers call
        /// this and pass the result to <c>GameObject.AddComponent</c>.
        /// </summary>
        public Type Resolve<T>() where T : Topic
        {
            return _typeOverrides.TryGetValue(typeof(T), out var ov) ? ov : typeof(T);
        }
    }
}
