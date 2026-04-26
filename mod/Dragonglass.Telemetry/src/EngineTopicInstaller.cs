// Scene-scoped installer for EngineTopic.
//
// Same lifecycle as FlightTopicInstaller (see there for the shape):
// Squad spawns a fresh instance on Flight scene entry and destroys
// it on scene exit, so the "engines" topic only exists while a
// vessel is flyable.

using Dragonglass.Telemetry.Topics;
using UnityEngine;

namespace Dragonglass.Telemetry
{
    [KSPAddon(KSPAddon.Startup.Flight, once: false)]
    public class EngineTopicInstaller : MonoBehaviour
    {
        private const string LogPrefix = "[Dragonglass/Telemetry] ";

        private EngineTopic _attached;

        private void Start()
        {
            GameObject host = GameObject.Find("Dragonglass.Telemetry");
            if (host == null)
            {
                Debug.LogWarning(LogPrefix + "no telemetry host GameObject; EngineTopic will not be installed");
                return;
            }
            // Route through TopicRegistry so a downstream mod can
            // swap in a subclass via `RegisterOverride<EngineTopic, X>()`.
            _attached = (EngineTopic)host.AddComponent(
                TopicRegistry.Instance.Resolve<EngineTopic>());
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
