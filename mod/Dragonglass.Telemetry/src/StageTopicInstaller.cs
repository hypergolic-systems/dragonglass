// Scene-scoped installers for StageTopic. Two installers, one per
// scene (Flight and EditorAny for VAB+SPH), since KSPAddon.Startup has
// no unified bucket. StageTopic itself is scene-aware — it branches on
// HighLogic.LoadedScene when sampling so the same instance works in
// either scene.

using Dragonglass.Telemetry.Topics;
using UnityEngine;

namespace Dragonglass.Telemetry
{
    internal static class StageTopicInstallerCore
    {
        private const string LogPrefix = "[Dragonglass/Telemetry] ";
        private const string HostName = "Dragonglass.Telemetry";

        public static StageTopic Attach()
        {
            GameObject host = GameObject.Find(HostName);
            if (host == null)
            {
                Debug.LogWarning(LogPrefix + "no telemetry host GameObject; StageTopic will not be installed");
                return null;
            }
            return host.AddComponent<StageTopic>();
        }

        public static void Detach(ref StageTopic attached)
        {
            if (attached != null)
            {
                Object.Destroy(attached);
                attached = null;
            }
        }
    }

    [KSPAddon(KSPAddon.Startup.Flight, once: false)]
    public class StageTopicInstaller : MonoBehaviour
    {
        private StageTopic _attached;

        private void Start() { _attached = StageTopicInstallerCore.Attach(); }

        private void OnDestroy() { StageTopicInstallerCore.Detach(ref _attached); }
    }

    [KSPAddon(KSPAddon.Startup.EditorAny, once: false)]
    public class StageTopicEditorInstaller : MonoBehaviour
    {
        private StageTopic _attached;

        private void Start() { _attached = StageTopicInstallerCore.Attach(); }

        private void OnDestroy() { StageTopicInstallerCore.Detach(ref _attached); }
    }
}
