// Scene-scoped installer for PartCatalogTopic. Editor-only, matching
// the shape of PawTopicEditorInstaller / StageTopicEditorInstaller.
// Parts only matter in the VAB/SPH; attaching in Flight would be
// wasted bytes (the client doesn't render a catalog panel there).

using Dragonglass.Telemetry.Topics;
using UnityEngine;

namespace Dragonglass.Telemetry
{
    [KSPAddon(KSPAddon.Startup.EditorAny, once: false)]
    public class PartCatalogInstaller : MonoBehaviour
    {
        private const string LogPrefix = "[Dragonglass/Telemetry] ";
        private const string HostName = "Dragonglass.Telemetry";

        private PartCatalogTopic _attached;

        private void Start()
        {
            GameObject host = GameObject.Find(HostName);
            if (host == null)
            {
                Debug.LogWarning(LogPrefix + "no telemetry host GameObject; PartCatalogTopic will not be installed");
                return;
            }
            _attached = host.AddComponent<PartCatalogTopic>();
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
