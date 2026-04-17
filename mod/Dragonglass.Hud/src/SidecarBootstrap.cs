// Launches the CEF sidecar as soon as KSP starts.
//
// Startup.Instantly + once=true means this runs exactly once, before
// the main menu even appears. The sidecar is ready by the time the
// player enters Flight.

using UnityEngine;

namespace Dragonglass.Hud
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class SidecarBootstrap : MonoBehaviour
    {
        void Awake()
        {
            DontDestroyOnLoad(gameObject);
            SidecarHost.EnsureRunning();
        }
    }
}
