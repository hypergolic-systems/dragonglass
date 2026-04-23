// Per-part telemetry. One PartTopic instance exists while the UI has a
// PAW open for that part — see PartSubscriptionManager for the
// add / remove wiring.
//
// The component lives on the Part's own GameObject. Unity handles
// the lifetime for free: when KSP destroys the Part (stage-off,
// explosion, unload) the PartTopic goes with it — OnDisable fires,
// the topic unregisters, and no stale pointer is ever seen. Sibling
// access to the Part is a plain GetComponent<Part>() on the same
// GameObject; no dictionaries, no lookups by persistentId.
//
// The topic name carries the KSP `Part.persistentId`:
//   Name = "part/<persistentId>"
// which is how client code subscribes to a specific part. The name is
// derived in OnEnable from the sibling Part's persistentId, so there
// is no separate Setup step — AddComponent-and-go.
//
// Wire format (positional):
//   data: [persistentId, name, [screenX, screenY, visible],
//          [[resourceName, abbr, available, capacity], ...]]
//
//     persistentId : string, decimal KSP Part.persistentId.
//     name         : string, localized part title (Part.partInfo.title).
//     screenX/Y    : numbers, CEF-viewport px (top-left origin, same
//                    frame the HUD's mouse-forwarding uses).
//     visible      : bool, false when the part is behind the camera.
//     resource row : [resourceName, abbr, available, capacity]
//                    amounts in stock units; capacity > 0.
//
// Dead-zoning: emit when screen position moves > 0.5 px or any
// resource amount shifts by > 0.5% of capacity. Follows the
// FlightTopic / EngineTopic jitter-suppression convention.

using System.Collections.Generic;
using System.Text;
using Dragonglass.Telemetry.Util;
using UnityEngine;

namespace Dragonglass.Telemetry.Topics
{
    public sealed class PartTopic : Topic
    {
        private const string LogPrefix = "[Dragonglass/Telemetry] ";

        private const float ScreenEpsilonPx = 0.5f;
        private const double ResourceFractionEpsilon = 0.005;  // 0.5% of capacity

        private struct ResourceFrame
        {
            public string ResourceName;
            public string Abbr;
            public double Available;
            public double Capacity;
        }

        private Part _part;
        private string _name = "part/unknown";
        private string _persistentIdStr = "";

        private string _partTitle = "";
        private float _screenX;
        private float _screenY;
        private bool _visible;
        private readonly List<ResourceFrame> _resources = new List<ResourceFrame>();
        private readonly List<ResourceFrame> _scratch = new List<ResourceFrame>();

        public override string Name { get { return _name; } }

        protected override void OnEnable()
        {
            // Sibling Part on the same GameObject — AddComponent<PartTopic>
            // was called against a Part's GameObject, so GetComponent<Part>
            // is the Part we sample.
            _part = GetComponent<Part>();
            if (_part != null)
            {
                _persistentIdStr = _part.persistentId.ToString();
                _name = "part/" + _persistentIdStr;
            }
            else
            {
                Debug.LogWarning(LogPrefix + "PartTopic added to a GameObject without a Part component; nothing to sample");
            }
            base.OnEnable();
            // Initial sample so the first broadcaster flush carries
            // real data. Without this the first frame could ship
            // while _partTitle is still "" and _resources still empty
            // — Unity's first Update call on a newly-added component
            // doesn't happen until the next frame, so OnEnable running
            // + a broadcaster tick on the same frame would otherwise
            // emit a placeholder.
            SampleFrame();
        }

        private void Update()
        {
            SampleFrame();
        }

        private void SampleFrame()
        {
            if (_part == null) return;

            bool changed = false;

            // Part title — localized. Stable over the part's lifetime
            // but cheap to diff each frame.
            string title = _part.partInfo != null ? _part.partInfo.title : _part.name;
            if (title == null) title = "";
            if (title != _partTitle) { _partTitle = title; changed = true; }

            // Screen position. CEF-viewport pixels, top-left origin —
            // matches the HUD's mouse-forwarding frame.
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (cam != null && _part.transform != null)
            {
                Vector3 screen = cam.WorldToScreenPoint(_part.transform.position);
                float nx = screen.x;
                float ny = Screen.height - screen.y;
                bool nv = screen.z > 0f;
                if (Mathf.Abs(nx - _screenX) > ScreenEpsilonPx) { _screenX = nx; changed = true; }
                if (Mathf.Abs(ny - _screenY) > ScreenEpsilonPx) { _screenY = ny; changed = true; }
                if (nv != _visible) { _visible = nv; changed = true; }
            }

            // Resources. Sample into scratch, compare positionally
            // against the last published list.
            _scratch.Clear();
            PartResourceList rl = _part.Resources;
            if (rl != null)
            {
                for (int i = 0; i < rl.Count; i++)
                {
                    PartResource r = rl[i];
                    if (r == null || r.info == null) continue;
                    _scratch.Add(new ResourceFrame
                    {
                        ResourceName = r.info.name,
                        Abbr = r.info.abbreviation,
                        Available = r.amount,
                        Capacity = r.maxAmount,
                    });
                }
            }
            if (ResourcesChanged(_resources, _scratch))
            {
                _resources.Clear();
                for (int i = 0; i < _scratch.Count; i++) _resources.Add(_scratch[i]);
                changed = true;
            }

            if (changed) MarkDirty();
        }

        private static bool ResourcesChanged(List<ResourceFrame> prev, List<ResourceFrame> next)
        {
            if (prev.Count != next.Count) return true;
            for (int i = 0; i < next.Count; i++)
            {
                ResourceFrame a = prev[i];
                ResourceFrame b = next[i];
                if (a.ResourceName != b.ResourceName) return true;
                if (a.Abbr != b.Abbr) return true;
                if (a.Capacity != b.Capacity) return true;
                double cap = b.Capacity > 0 ? b.Capacity : 1;
                if (System.Math.Abs(a.Available - b.Available) / cap > ResourceFractionEpsilon)
                    return true;
            }
            return false;
        }

        public override void WriteData(StringBuilder sb)
        {
            sb.Append('[');
            Json.WriteString(sb, _persistentIdStr);
            sb.Append(',');
            Json.WriteString(sb, _partTitle);
            sb.Append(',');
            sb.Append('[');
            Json.WriteFloat(sb, _screenX);
            sb.Append(',');
            Json.WriteFloat(sb, _screenY);
            sb.Append(',');
            Json.WriteBool(sb, _visible);
            sb.Append(']');
            sb.Append(',');
            sb.Append('[');
            for (int i = 0; i < _resources.Count; i++)
            {
                if (i > 0) sb.Append(',');
                ResourceFrame r = _resources[i];
                sb.Append('[');
                Json.WriteString(sb, r.ResourceName);
                sb.Append(',');
                Json.WriteString(sb, r.Abbr);
                sb.Append(',');
                Json.WriteDouble(sb, r.Available);
                sb.Append(',');
                Json.WriteDouble(sb, r.Capacity);
                sb.Append(']');
            }
            sb.Append(']');
            sb.Append(']');
        }
    }
}
