// Scene-scoped installers for PawTopic. Same shape as
// FlightTopicInstaller / StageTopicInstaller — attaches PawTopic to
// the persistent telemetry host while the target scene is loaded;
// Unity tears it down on scene exit.
//
// Two installers, one per scene (Flight and EditorAny for VAB+SPH),
// because KSPAddon.Startup has no "flight or editor" bucket. The
// always-on PartSubscriptionManager (installed by TelemetryAddon)
// handles the per-part sampler lifecycle independently.

using Dragonglass.Telemetry.Topics;
using UnityEngine;

namespace Dragonglass.Telemetry
{
    internal static class PawTopicInstallerCore
    {
        private const string LogPrefix = "[Dragonglass/Telemetry] ";
        private const string HostName = "Dragonglass.Telemetry";

        public static PawTopic Attach()
        {
            GameObject host = GameObject.Find(HostName);
            if (host == null)
            {
                Debug.LogWarning(LogPrefix + "no telemetry host GameObject; PawTopic will not be installed");
                return null;
            }
            return host.AddComponent<PawTopic>();
        }

        public static void Detach(ref PawTopic attached)
        {
            if (attached != null)
            {
                Object.Destroy(attached);
                attached = null;
            }
        }
    }

    [KSPAddon(KSPAddon.Startup.Flight, once: false)]
    public class PawTopicInstaller : MonoBehaviour
    {
        private PawTopic _attached;

        private void Start() { _attached = PawTopicInstallerCore.Attach(); }

        private void OnDestroy() { PawTopicInstallerCore.Detach(ref _attached); }
    }

    [KSPAddon(KSPAddon.Startup.EditorAny, once: false)]
    public class PawTopicEditorInstaller : MonoBehaviour
    {
        private PawTopic _attached;

        private void Start() { _attached = PawTopicInstallerCore.Attach(); }

        private void OnDestroy() { PawTopicInstallerCore.Detach(ref _attached); }
    }
}
