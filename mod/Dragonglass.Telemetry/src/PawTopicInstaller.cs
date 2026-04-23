// Scene-scoped installer for PawTopic. Same shape as
// FlightTopicInstaller / StageTopicInstaller — attaches PawTopic to
// the persistent telemetry host while the Flight scene is loaded;
// Unity tears it down on scene exit. PawTopic is Flight-only because
// right-click events only fire from Flight anyway; the always-on
// PartSubscriptionManager (installed by TelemetryAddon) handles the
// per-part sampler lifecycle independently.

using Dragonglass.Telemetry.Topics;
using UnityEngine;

namespace Dragonglass.Telemetry
{
    [KSPAddon(KSPAddon.Startup.Flight, once: false)]
    public class PawTopicInstaller : MonoBehaviour
    {
        private const string LogPrefix = "[Dragonglass/Telemetry] ";

        private PawTopic _attached;

        private void Start()
        {
            GameObject host = GameObject.Find("Dragonglass.Telemetry");
            if (host == null)
            {
                Debug.LogWarning(LogPrefix + "no telemetry host GameObject; PawTopic will not be installed");
                return;
            }
            _attached = host.AddComponent<PawTopic>();
        }

        private void OnDestroy()
        {
            if (_attached != null)
            {
                Destroy(_attached);
                _attached = null;
            }
        }
    }
}
