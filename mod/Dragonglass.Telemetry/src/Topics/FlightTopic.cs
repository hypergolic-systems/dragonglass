// Active-vessel flight telemetry. Exists only while the Flight scene
// is loaded — `FlightTopicInstaller` adds this component on scene
// entry and Unity tears it down on scene exit, so the `flight` topic
// simply doesn't exist in main menu / Space Center / VAB / Tracking
// Station.
//
// Coordinate-frame conversion — important.
//
// Unity is **left-handed** Y-up (X-right, Y-up, Z-forward). Most 3D
// graphics libraries on the browser side (three.js, babylon) are
// **right-handed** Y-up (X-right, Y-up, Z-backward). We convert all
// orientation/rotation data to right-handed at the wire so clients
// don't have to think about Unity's handedness.
//
// For a Z-axis reflection (LH Y-up → RH Y-up):
//   - Regular vectors:     (x, y, z)          → (x, y, -z)
//   - Quaternions:         (qx, qy, qz, qw)   → (-qx, -qy, qz, qw)
//   - Pseudovectors (ω):   (ωx, ωy, ωz)       → (-ωx, -ωy, ωz)
//
// The quaternion rule comes from: the axis Z-flips, and the rotation
// direction flips (LH→RH convention), so the angle sign flips, so
// sin(θ/2) flips → the vector part picks up a net negation, except
// the Z component which flips twice and ends up unchanged.
// Angular velocity is a pseudovector and transforms the same way.
//
// Wire format (positional array):
//   data: [vesselId, altAsl, altRadar, vSurf, vOrb, vVert,
//          throttle, sas, rcs, [qx,qy,qz,qw], [wx,wy,wz]]
//
//     vesselId  : string GUID of the active vessel
//     altAsl    : altitude above sea level (meters)
//     altRadar  : altitude above terrain (meters)
//     vSurf     : surface speed (m/s)
//     vOrb      : orbital speed (m/s)
//     vVert     : vertical speed (m/s), signed (positive = climbing)
//     throttle  : main-engine throttle [0, 1]
//     sas       : SAS action group state
//     rcs       : RCS action group state
//     [q...]    : world-to-body orientation quaternion, right-handed Y-up
//     [w...]    : angular velocity in body frame, rad/s, right-handed

using System.Text;
using Dragonglass.Telemetry.Util;
using UnityEngine;

namespace Dragonglass.Telemetry.Topics
{
    public sealed class FlightTopic : Topic
    {
        public override string Name { get { return "flight"; } }

        private string _vesselId = "";
        public string VesselId
        {
            get { return _vesselId; }
            set { if (_vesselId != value) { _vesselId = value; MarkDirty(); } }
        }

        private double _altitudeAsl;
        public double AltitudeAsl
        {
            get { return _altitudeAsl; }
            set { if (_altitudeAsl != value) { _altitudeAsl = value; MarkDirty(); } }
        }

        private double _altitudeRadar;
        public double AltitudeRadar
        {
            get { return _altitudeRadar; }
            set { if (_altitudeRadar != value) { _altitudeRadar = value; MarkDirty(); } }
        }

        private double _surfaceSpeed;
        public double SurfaceSpeed
        {
            get { return _surfaceSpeed; }
            set { if (_surfaceSpeed != value) { _surfaceSpeed = value; MarkDirty(); } }
        }

        private double _orbitalSpeed;
        public double OrbitalSpeed
        {
            get { return _orbitalSpeed; }
            set { if (_orbitalSpeed != value) { _orbitalSpeed = value; MarkDirty(); } }
        }

        private double _verticalSpeed;
        public double VerticalSpeed
        {
            get { return _verticalSpeed; }
            set { if (_verticalSpeed != value) { _verticalSpeed = value; MarkDirty(); } }
        }

        private float _throttle;
        public float Throttle
        {
            get { return _throttle; }
            set { if (_throttle != value) { _throttle = value; MarkDirty(); } }
        }

        private bool _sas;
        public bool Sas
        {
            get { return _sas; }
            set { if (_sas != value) { _sas = value; MarkDirty(); } }
        }

        private bool _rcs;
        public bool Rcs
        {
            get { return _rcs; }
            set { if (_rcs != value) { _rcs = value; MarkDirty(); } }
        }

        private Quaternion _orientation = Quaternion.identity;
        public Quaternion Orientation
        {
            get { return _orientation; }
            set { if (_orientation != value) { _orientation = value; MarkDirty(); } }
        }

        private Vector3 _angularVelocity;
        public Vector3 AngularVelocity
        {
            get { return _angularVelocity; }
            set { if (_angularVelocity != value) { _angularVelocity = value; MarkDirty(); } }
        }

        private void Update()
        {
            if (FlightGlobals.fetch == null) return;
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return;

            VesselId = v.id.ToString("D");
            AltitudeAsl = v.altitude;
            AltitudeRadar = v.radarAltitude;
            SurfaceSpeed = v.srfSpeed;
            OrbitalSpeed = v.obt_speed;
            VerticalSpeed = v.verticalSpeed;
            Throttle = v.ctrlState != null ? v.ctrlState.mainThrottle : 0f;
            Sas = v.ActionGroups[KSPActionGroup.SAS];
            Rcs = v.ActionGroups[KSPActionGroup.RCS];
            Orientation = v.ReferenceTransform != null
                ? v.ReferenceTransform.rotation
                : Quaternion.identity;
            AngularVelocity = v.angularVelocity;
        }

        public override void WriteData(StringBuilder sb)
        {
            sb.Append('[');
            Json.WriteString(sb, _vesselId);
            sb.Append(',');
            Json.WriteDouble(sb, _altitudeAsl);
            sb.Append(',');
            Json.WriteDouble(sb, _altitudeRadar);
            sb.Append(',');
            Json.WriteDouble(sb, _surfaceSpeed);
            sb.Append(',');
            Json.WriteDouble(sb, _orbitalSpeed);
            sb.Append(',');
            Json.WriteDouble(sb, _verticalSpeed);
            sb.Append(',');
            Json.WriteFloat(sb, _throttle);
            sb.Append(',');
            Json.WriteBool(sb, _sas);
            sb.Append(',');
            Json.WriteBool(sb, _rcs);
            sb.Append(',');

            // Orientation: LH → RH Y-up, quaternion convention (see header).
            sb.Append('[');
            Json.WriteFloat(sb, -_orientation.x);
            sb.Append(',');
            Json.WriteFloat(sb, -_orientation.y);
            sb.Append(',');
            Json.WriteFloat(sb, _orientation.z);
            sb.Append(',');
            Json.WriteFloat(sb, _orientation.w);
            sb.Append(']');
            sb.Append(',');

            // Angular velocity: pseudovector under Z-flip, same sign
            // pattern as the quaternion's vector part.
            sb.Append('[');
            Json.WriteFloat(sb, -_angularVelocity.x);
            sb.Append(',');
            Json.WriteFloat(sb, -_angularVelocity.y);
            sb.Append(',');
            Json.WriteFloat(sb, _angularVelocity.z);
            sb.Append(']');

            sb.Append(']');
        }
    }
}
