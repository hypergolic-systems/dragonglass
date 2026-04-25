// Launches the CEF sidecar as soon as KSP starts.
//
// Startup.Instantly + once=true means this runs exactly once, before
// the main menu even appears. The sidecar is ready by the time the
// player enters Flight.
//
// EnsureRunning is deferred one frame via a coroutine so other
// Startup.Instantly addons get a chance to call SidecarHost
// configuration APIs (e.g. OverrideEntry) during their own Awake
// before the sidecar is spawned with a frozen --entry= argument.

using System.Collections;
using UnityEngine;

namespace Dragonglass.Hud
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class SidecarBootstrap : MonoBehaviour
    {
        void Awake()
        {
            DontDestroyOnLoad(gameObject);
            StartCoroutine(SpawnNextFrame());
        }

        private IEnumerator SpawnNextFrame()
        {
            yield return null;
            SidecarHost.EnsureRunning();
        }
    }
}
