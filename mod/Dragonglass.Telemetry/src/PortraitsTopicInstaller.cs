// Flight-only installer for PortraitsTopic. The portrait gallery
// `KerbalPortraitGallery.Instance` only exists in the Flight scene,
// so the topic has no useful data to publish elsewhere.

using Dragonglass.Telemetry.Topics;
using UnityEngine;

namespace Dragonglass.Telemetry
{
    [KSPAddon(KSPAddon.Startup.Flight, once: false)]
    public class PortraitsTopicInstaller : MonoBehaviour
    {
        private const string LogPrefix = "[Dragonglass/Telemetry] ";
        private const string HostName = "Dragonglass.Telemetry";

        private PortraitsTopic _attached;

        private void Start()
        {
            GameObject host = GameObject.Find(HostName);
            if (host == null)
            {
                Debug.LogWarning(LogPrefix + "no telemetry host GameObject; PortraitsTopic will not be installed");
                return;
            }
            _attached = (PortraitsTopic)host.AddComponent(
                TopicRegistry.Instance.Resolve<PortraitsTopic>());
        }

        private void OnDestroy()
        {
            if (_attached != null)
            {
                Object.Destroy(_attached);
                _attached = null;
            }
        }
    }
}
