// UI-facing configuration file.
//
// Reads <modDir>/config.json (one directory up from the Telemetry DLL
// in Plugins/) once at startup and republishes its contents verbatim
// on the `config` topic. The plugin does NOT parse the JSON — each UI
// defines its own schema against the top-level keys it cares about
// (stock reads `editor` and `paw` booleans; other UIs may use any
// shape they like).
//
// Missing file → default payload `{}`. An unreadable file (IO error)
// logs and also falls back to `{}`. Malformed JSON is passed through
// as-is and will break downstream parsing — the user accepted that
// tradeoff to keep the plugin schema-agnostic.
//
// Static across the session: read once in Awake, marked dirty so the
// broadcaster flushes it on its next tick, and the broadcaster caches
// the frame for snapshot-on-connect replay. No Update loop.

using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace Dragonglass.Telemetry.Topics
{
    public sealed class ConfigTopic : Topic
    {
        public override string Name { get { return "config"; } }

        private string _payload = "{}";

        private void Awake()
        {
            string path = ResolvePath();
            if (path != null && File.Exists(path))
            {
                try
                {
                    _payload = File.ReadAllText(path);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[Dragonglass/Telemetry] config.json read failed (" +
                                     path + "): " + e.Message);
                }
            }
            MarkDirty();
        }

        public override void WriteData(StringBuilder sb)
        {
            sb.Append(_payload);
        }

        // Telemetry DLL lives at <modDir>/Plugins/Dragonglass.Telemetry.dll;
        // walk up one directory to sit alongside e.g. PluginData, Sidecar,
        // and the config file. Keyed off the DLL path rather than a
        // hardcoded "Dragonglass_Hud" so users who rename the mod folder
        // still get their config picked up.
        private static string ResolvePath()
        {
            try
            {
                string dll = Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrEmpty(dll)) return null;
                string pluginsDir = Path.GetDirectoryName(dll);
                string modDir = Path.GetDirectoryName(pluginsDir);
                return Path.Combine(modDir, "config.json");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Dragonglass/Telemetry] config.json path resolve failed: " + e.Message);
                return null;
            }
        }
    }
}
