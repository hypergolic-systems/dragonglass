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
        /// Write the topic's current state as a JSON object into
        /// <paramref name="sb"/>. Produces e.g. <c>{"scene":"FLIGHT",...}</c>.
        /// The broadcaster wraps this in the full envelope.
        /// </summary>
        public abstract void WriteData(StringBuilder sb);

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
