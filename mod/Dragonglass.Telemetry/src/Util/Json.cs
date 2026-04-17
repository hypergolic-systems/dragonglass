// Minimal JSON writer helpers for the telemetry wire format.
//
// We avoid bringing in Newtonsoft.Json even though KSP bundles a copy —
// the bundled version is old, and our needs are tiny. This handles the
// two things you can't eyeball: RFC 8259 string escaping (so a vessel
// name containing a `"` or a control char doesn't blow up the parser)
// and InvariantCulture number formatting (so a double never serialises
// as "1,234.5" on a German locale).

using System.Globalization;
using System.Text;

namespace Dragonglass.Telemetry.Util
{
    internal static class Json
    {
        public static void WriteString(StringBuilder sb, string s)
        {
            if (s == null) { sb.Append("null"); return; }
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b");  break;
                    case '\f': sb.Append("\\f");  break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        public static void WriteDouble(StringBuilder sb, double d)
        {
            // "R" round-trip format keeps full precision without the
            // locale baggage of "G".
            sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        public static void WriteLong(StringBuilder sb, long n)
        {
            sb.Append(n.ToString(CultureInfo.InvariantCulture));
        }

        public static void WriteBool(StringBuilder sb, bool b)
        {
            sb.Append(b ? "true" : "false");
        }

        public static void WriteNull(StringBuilder sb)
        {
            sb.Append("null");
        }
    }
}
