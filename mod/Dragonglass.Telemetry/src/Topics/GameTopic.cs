// Game-level discrete state: which scene KSP is in, the active
// vessel's ID, and the current timewarp multiplier. Unlike ClockTopic,
// these values only change at specific events (scene transitions,
// vessel switches, warp button presses), so the topic stays quiet for
// long stretches and only broadcasts on actual state changes.
//
// Wire format (positional array):
//   data: [scene, activeVesselId, timewarp, mapActive]
//     scene           : GameScenes enum as string, e.g. "FLIGHT"
//     activeVesselId  : GUID string of the active vessel, or null
//     timewarp        : multiplier as a number. 1-4 → physics warp;
//                       5+ → on-rails warp (5, 10, 50, 100, 1000, …)
//     mapActive       : true when the flight scene is in map view.
//                       Drives "hide PAWs" in the UI — stock KSP
//                       hides them too because the screen-space
//                       projection of physical parts isn't meaningful
//                       in map space.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dragonglass.Telemetry.Util;
using UnityEngine;

namespace Dragonglass.Telemetry.Topics
{
    public sealed class GameTopic : Topic
    {
        public override string Name { get { return "game"; } }

        private GameScenes _scene;
        public GameScenes Scene
        {
            get { return _scene; }
            set { if (_scene != value) { _scene = value; MarkDirty(); } }
        }

        private Guid? _activeVesselId;
        public Guid? ActiveVesselId
        {
            get { return _activeVesselId; }
            set { if (!Nullable.Equals(_activeVesselId, value)) { _activeVesselId = value; MarkDirty(); } }
        }

        private float _timewarp = 1f;
        public float Timewarp
        {
            get { return _timewarp; }
            set { if (_timewarp != value) { _timewarp = value; MarkDirty(); } }
        }

        private bool _mapActive;
        public bool MapActive
        {
            get { return _mapActive; }
            set { if (_mapActive != value) { _mapActive = value; MarkDirty(); } }
        }

        private void Update()
        {
            Scene = HighLogic.LoadedScene;
            if (FlightGlobals.fetch != null)
            {
                Vessel active = FlightGlobals.ActiveVessel;
                ActiveVesselId = active != null ? active.id : (Guid?)null;
            }
            else
            {
                ActiveVesselId = null;
            }
            Timewarp = TimeWarp.CurrentRate;
            // Stock's `MapView.MapIsEnabled` is a static bool the rest
            // of KSP keys off; it flips on M / map-button press in
            // Flight, stays false otherwise.
            MapActive = HighLogic.LoadedScene == GameScenes.FLIGHT
                && MapView.MapIsEnabled;
        }

        public override void HandleOp(string op, List<object> args)
        {
            switch (op)
            {
                case "setCapabilities":
                    if (args.Count == 1 && args[0] is List<object> list)
                        Capabilities.Set(list.OfType<string>());
                    else
                        Debug.LogWarning("[Dragonglass/Telemetry] GameTopic.setCapabilities: expected [string[]]");
                    break;
                default:
                    base.HandleOp(op, args);
                    break;
            }
        }

        public override void WriteData(StringBuilder sb)
        {
            sb.Append('[');
            Json.WriteString(sb, _scene.ToString());
            sb.Append(',');
            if (_activeVesselId.HasValue)
                Json.WriteString(sb, _activeVesselId.Value.ToString("D"));
            else
                Json.WriteNull(sb);
            sb.Append(',');
            Json.WriteDouble(sb, _timewarp);
            sb.Append(',');
            sb.Append(_mapActive ? "true" : "false");
            sb.Append(']');
        }
    }
}
