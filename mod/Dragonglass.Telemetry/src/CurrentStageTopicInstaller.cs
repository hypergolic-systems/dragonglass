// Scene-scoped installer for CurrentStageTopic.
//
// Same lifecycle as FlightTopicInstaller / EngineTopicInstaller —
// Squad spawns a fresh instance on Flight scene entry and destroys
// it on scene exit, so the "currentStage" topic only exists while
// a vessel is flyable.

using Dragonglass.Telemetry.Topics;
using UnityEngine;

namespace Dragonglass.Telemetry
{
    [KSPAddon(KSPAddon.Startup.Flight, once: false)]
    public class CurrentStageTopicInstaller : MonoBehaviour
    {
        private const string LogPrefix = "[Dragonglass/Telemetry] ";

        private CurrentStageTopic _attached;

        private void Start()
        {
            GameObject host = GameObject.Find("Dragonglass.Telemetry");
            if (host == null)
            {
                Debug.LogWarning(LogPrefix + "no telemetry host GameObject; CurrentStageTopic will not be installed");
                return;
            }
            _attached = host.AddComponent<CurrentStageTopic>();
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
