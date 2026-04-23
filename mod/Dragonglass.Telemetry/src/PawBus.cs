// Static event bridge between the Hud assembly's Harmony patch (which
// has access to UIPartActionController) and the Telemetry assembly's
// PawTopic (which owns wire broadcasting). Dragonglass.Hud already
// references Dragonglass.Telemetry transitively via the plugin
// bootstrap; this bus keeps the dependency direction clean — the
// patch just fires a static event, and PawTopic subscribes to it
// when it comes alive in the Flight scene.
//
// Semantics: every Raise() is an independent right-click pulse.
// Consumers treat duplicate raises for the same persistentId as
// meaningful (the pilot clicked the same part twice — the UI should
// raise that PAW to the top).

using System;

namespace Dragonglass.Telemetry
{
    public static class PawBus
    {
        /// <summary>
        /// Raised whenever the pilot right-clicks a part in the
        /// Flight scene, carrying the part's
        /// <see cref="Part.persistentId"/>. Subscribers run on Unity's
        /// main thread (the patch fires inside KSP's own input
        /// handling).
        /// </summary>
        public static event Action<uint> PartRightClicked;

        public static void Raise(uint persistentId)
        {
            Action<uint> handler = PartRightClicked;
            if (handler != null) handler(persistentId);
        }
    }
}
