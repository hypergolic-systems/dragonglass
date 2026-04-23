// Base class for every telemetry topic.
//
// A Topic is a Unity MonoBehaviour; drop it onto a GameObject via
// AddComponent<T>() and Unity drives its lifecycle: Awake/Start for
// setup, Update each frame to sample source data, OnDestroy (and
// OnDisable) to tear down. Topics self-register with the
// TopicRegistry in OnEnable and unregister in OnDisable, so a topic
// is in the registry iff its component is alive and enabled.
//
// Concrete topics expose typed properties whose setters compare the
// new value to the current one and call MarkDirty() only when it
// actually changed. The broadcaster flushes dirty topics at a fixed
// cadence and clears the flag; if nothing moved between flushes,
// nothing goes on the wire.

using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Dragonglass.Telemetry.Topics
{
    public abstract class Topic : MonoBehaviour
    {
        /// <summary>
        /// Wire-level topic name (e.g. "game"). Used as the map key in
        /// the registry and as the "topic" field in every broadcast
        /// envelope. Must be unique across all topics.
        /// </summary>
        public abstract string Name { get; }

        /// <summary>
        /// True iff at least one field has changed since the last
        /// broadcaster flush. Cleared by <see cref="ClearDirty"/>.
        /// </summary>
        public bool IsDirty { get; private set; }

        /// <summary>
        /// True for one-shot event topics whose payload is a pulse,
        /// not a state snapshot. The broadcaster still flushes these
        /// on dirty, but does NOT cache the frame for replay to new
        /// clients — otherwise a reconnecting client would receive
        /// the last event as "current state" and, e.g., re-open a
        /// PAW that the pilot right-clicked minutes ago. Default is
        /// false; only override when the topic's semantics really
        /// are event-driven rather than state-driven.
        /// </summary>
        public virtual bool IsEvent { get { return false; } }

        /// <summary>
        /// Write the topic's current state as a JSON object into
        /// <paramref name="sb"/>. Produces e.g. <c>{"scene":"FLIGHT",...}</c>.
        /// The broadcaster wraps this in the full envelope.
        /// </summary>
        public abstract void WriteData(StringBuilder sb);

        /// <summary>
        /// Dispatch an inbound op (e.g. <c>setSas</c>) parsed from a
        /// <c>{"topic":"...","op":"...","args":[...]}</c> envelope.
        /// Invoked on Unity's main thread by <see cref="OpDispatcher"/>,
        /// so implementations may freely touch KSP state.
        /// Default is log + ignore; override per-topic to add behavior.
        /// </summary>
        public virtual void HandleOp(string op, List<object> args)
        {
            Debug.LogWarning("[Dragonglass/Telemetry] topic '" + Name +
                             "' has no op handler (got '" + op + "')");
        }

        protected virtual void OnEnable()
        {
            TopicRegistry.Instance?.Register(this);
        }

        protected virtual void OnDisable()
        {
            TopicRegistry.Instance?.Unregister(this);
        }

        protected void MarkDirty() { IsDirty = true; }
        internal void ClearDirty() { IsDirty = false; }
    }
}
