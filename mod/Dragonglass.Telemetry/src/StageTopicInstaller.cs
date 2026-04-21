// Scene-scoped installer for StageTopic. Same lifecycle as
// FlightTopicInstaller / EngineTopicInstaller — see FlightTopicInstaller
// for the shape rationale. Attaches a StageTopic component to the
// persistent telemetry host while the Flight scene is loaded; Unity
// tears it down on scene exit.

using Dragonglass.Telemetry.Topics;
using UnityEngine;

namespace Dragonglass.Telemetry
{
    [KSPAddon(KSPAddon.Startup.Flight, once: false)]
    public class StageTopicInstaller : MonoBehaviour
    {
        private const string LogPrefix = "[Dragonglass/Telemetry] ";

        private StageTopic _attached;

        private void Start()
        {
            GameObject host = GameObject.Find("Dragonglass.Telemetry");
            if (host == null)
            {
                Debug.LogWarning(LogPrefix + "no telemetry host GameObject; StageTopic will not be installed");
                return;
            }
            _attached = host.AddComponent<StageTopic>();
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
