// Clock telemetry: universal time and mission elapsed time.
//
// Sampled every Unity frame; both values tick continuously whenever
// physics is advancing, so expect this topic to broadcast at the
// broadcaster's flush cadence (10 Hz) during active play. Goes quiet
// in menus, when paused, and in Space Center / VAB where time is
// frozen.
//
// Wire format (positional array):
//   data: [ut, met]
//     ut  : universal time in seconds since game start (double)
//     met : mission elapsed time for the active vessel in seconds
//           (double), or null when no vessel is active

using System;
using System.Text;
using Dragonglass.Telemetry.Util;

namespace Dragonglass.Telemetry.Topics
{
    public sealed class ClockTopic : Topic
    {
        public override string Name { get { return "clock"; } }

        private double _ut;
        public double Ut
        {
            get { return _ut; }
            set { if (_ut != value) { _ut = value; MarkDirty(); } }
        }

        private double? _met;
        public double? Met
        {
            get { return _met; }
            set { if (!Nullable.Equals(_met, value)) { _met = value; MarkDirty(); } }
        }

        private void Update()
        {
            if (Planetarium.fetch != null)
            {
                Ut = Planetarium.GetUniversalTime();
            }
            if (FlightGlobals.fetch != null)
            {
                Vessel active = FlightGlobals.ActiveVessel;
                Met = active != null ? active.missionTime : (double?)null;
            }
            else
            {
                Met = null;
            }
        }

        public override void WriteData(StringBuilder sb)
        {
            sb.Append('[');
            Json.WriteDouble(sb, _ut);
            sb.Append(',');
            if (_met.HasValue) Json.WriteDouble(sb, _met.Value);
            else Json.WriteNull(sb);
            sb.Append(']');
        }
    }
}
