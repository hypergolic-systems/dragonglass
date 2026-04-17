// Scene-scoped installer for FlightTopic.
//
// KSPAddon with Startup.Flight + once: false means Squad spawns a
// fresh instance every time the Flight scene loads and destroys it on
// scene exit. We use that to attach a FlightTopic component to the
// persistent TelemetryAddon GameObject only while flight is the
// active scene — when the player returns to Space Center / main menu,
// Unity destroys this installer's GameObject, which triggers our
// OnDestroy to remove the FlightTopic component from the telemetry
// host, which triggers the topic's OnDisable to unregister.
//
// Net effect: the "flight" topic only exists (and only shows up in
// snapshot replays to new clients) while a vessel is flyable.

using Dragonglass.Telemetry.Topics;
using UnityEngine;

namespace Dragonglass.Telemetry
{
    [KSPAddon(KSPAddon.Startup.Flight, once: false)]
    public class FlightTopicInstaller : MonoBehaviour
    {
        private const string LogPrefix = "[Dragonglass/Telemetry] ";

        private FlightTopic _attached;

        private void Start()
        {
            GameObject host = GameObject.Find("Dragonglass.Telemetry");
            if (host == null)
            {
                Debug.LogWarning(LogPrefix + "no telemetry host GameObject; FlightTopic will not be installed");
                return;
            }
            _attached = host.AddComponent<FlightTopic>();
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
