// Active-vessel flight telemetry. Exists only while the Flight scene
// is loaded — `FlightTopicInstaller` adds this component on scene
// entry and Unity tears it down on scene exit, so the `flight` topic
// simply doesn't exist in main menu / Space Center / VAB / Tracking
// Station.
//
// Coordinate frames.
//
// All directional data on the wire is expressed in a **surface frame**
// anchored at the vessel:
//
//   +Y = planet-radial up      (ZENITH)
//   +Z = north, tangent        (compass N)
//   +X = east                  (compass E)
//
// This is stable (Krakensbane / Unity world drift cancels out) and
// lines up with the navball sphere's texture convention on the
// client. Velocity vectors are shipped directly in this frame; the
// client places orbital markers at `normalize(velocity)` on the
// sphere and derives speed from the vector magnitude.
//
// The attitude quaternion represents the same surface frame on the
// right side; on the left it has a body-axis remap (BodyToWire) so
// the vessel's nose direction is body-wire +Z (three.js "forward")
// instead of KSP's body +Y (up-stack).
//
// Handedness. Unity is LH Y-up; three.js is RH Y-up. Empirically
// (verified against live navball axes + textures), shipping the
// surface-frame components as-is works because the body and surface
// frames flip handedness identically, and the sphere texture is
// drawn to match the resulting RH interpretation.
//
// Wire format (positional array):
//   data: [vesselId, altAsl, altRadar, [vSurf...], [vOrb...],
//          throttle, sas, rcs, [qx,qy,qz,qw], [wx,wy,wz]]
//
//     vesselId   : string GUID of the active vessel
//     altAsl     : altitude above sea level (meters)
//     altRadar   : altitude above terrain (meters)
//     [vSurf...] : surface velocity vector in surface frame, m/s
//                  (relative to the rotating planet surface)
//     [vOrb...]  : orbital velocity vector in surface frame, m/s
//                  (relative to the planet's inertial frame)
//     throttle   : main-engine throttle [0, 1]
//     sas        : SAS action group state
//     rcs        : RCS action group state
//     [q...]     : vessel orientation in surface frame (body-wire
//                  basis: +Z = nose, +Y = dorsal, +X = starboard)
//     [w...]     : angular velocity in vessel body frame, rad/s

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

        private Vector3 _surfaceVelocity;
        public Vector3 SurfaceVelocity
        {
            get { return _surfaceVelocity; }
            set { if (_surfaceVelocity != value) { _surfaceVelocity = value; MarkDirty(); } }
        }

        private Vector3 _orbitalVelocity;
        public Vector3 OrbitalVelocity
        {
            get { return _orbitalVelocity; }
            set { if (_orbitalVelocity != value) { _orbitalVelocity = value; MarkDirty(); } }
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
            Throttle = v.ctrlState != null ? v.ctrlState.mainThrottle : 0f;
            Sas = v.ActionGroups[KSPActionGroup.SAS];
            Rcs = v.ActionGroups[KSPActionGroup.RCS];

            Quaternion surface = SurfaceRotation(v);
            Quaternion surfaceInverse = Quaternion.Inverse(surface);

            SurfaceVelocity = surfaceInverse * (Vector3)v.srf_velocity;
            OrbitalVelocity = surfaceInverse * (Vector3)v.obt_velocity;

            Orientation = v.ReferenceTransform != null
                ? surfaceInverse * v.ReferenceTransform.rotation * BodyToWire
                : Quaternion.identity;
            AngularVelocity = BodyToWire * v.angularVelocity;
        }

        // KSP parts (including command/probe cores that drive the
        // `ReferenceTransform`) are modelled with +Y = nose / up-stack
        // and -Y = engines / down-stack. Standard 3D convention (which
        // the client-side navball sphere uses) expects +Z = forward
        // (nose). The remap below takes body +Z → body +Y so the wire
        // quaternion has its "forward" axis where three.js expects it.
        //
        // Without this remap, a roll around the vessel's nose is a
        // rotation around body +Y, which the client interprets as a
        // rotation around the sphere's vertical axis — i.e. yaw — so
        // rolling the craft spins the compass tape instead of tilting
        // the horizon.
        //
        // `BodyToWire` is a -90° rotation around +X. Applied as a
        // post-multiplication on the body-to-surface quaternion it
        // remaps the body basis; applied directly to the body-frame
        // angular velocity vector it permutes the components the same
        // way so roll-rate lands in the wire's Z slot.
        private static readonly Quaternion BodyToWire =
            Quaternion.AngleAxis(-90f, Vector3.right);

        // Build the rotation that takes surface-frame vectors to
        // Unity world-frame vectors, with surface conventions:
        //   +Z_surface = north (tangent to the surface)
        //   +Y_surface = up    (radially outward from planet center)
        //   +X_surface = east
        //
        // Anchored on the vessel's current position (v.upAxis) and the
        // current body (v.mainBody.transform.up as planet spin axis).
        // Used for both the attitude quaternion and the velocity
        // vectors — everything directional on the wire rides on this.
        //
        // Degenerate case: at a pole, the planet spin axis is colinear
        // with the radial up vector, so projecting "north" onto the
        // horizontal plane yields zero. We fall back to identity;
        // heading will be undefined at the pole while pitch and roll
        // still make sense.
        private static Quaternion SurfaceRotation(Vessel v)
        {
            Vector3 up = v.upAxis;
            Vector3 planetNorth = v.mainBody != null
                ? v.mainBody.transform.up
                : Vector3.up;
            Vector3 north = Vector3.ProjectOnPlane(planetNorth, up);
            if (north.sqrMagnitude < 1e-8f) return Quaternion.identity;
            return Quaternion.LookRotation(north.normalized, up);
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
            WriteVec3(sb, _surfaceVelocity);
            sb.Append(',');
            WriteVec3(sb, _orbitalVelocity);
            sb.Append(',');
            Json.WriteFloat(sb, _throttle);
            sb.Append(',');
            Json.WriteBool(sb, _sas);
            sb.Append(',');
            Json.WriteBool(sb, _rcs);
            sb.Append(',');

            sb.Append('[');
            Json.WriteFloat(sb, _orientation.x);
            sb.Append(',');
            Json.WriteFloat(sb, _orientation.y);
            sb.Append(',');
            Json.WriteFloat(sb, _orientation.z);
            sb.Append(',');
            Json.WriteFloat(sb, _orientation.w);
            sb.Append(']');
            sb.Append(',');

            WriteVec3(sb, _angularVelocity);

            sb.Append(']');
        }

        private static void WriteVec3(StringBuilder sb, Vector3 v)
        {
            sb.Append('[');
            Json.WriteFloat(sb, v.x);
            sb.Append(',');
            Json.WriteFloat(sb, v.y);
            sb.Append(',');
            Json.WriteFloat(sb, v.z);
            sb.Append(']');
        }
    }
}
