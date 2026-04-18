// Minimal JSON writer + reader helpers for the telemetry wire format.
//
// We avoid bringing in Newtonsoft.Json even though KSP bundles a copy —
// the bundled version is old, and our needs are tiny. Writers handle the
// two things you can't eyeball: RFC 8259 string escaping (so a vessel
// name containing a `"` or a control char doesn't blow up the parser)
// and InvariantCulture number formatting (so a double never serialises
// as "1,234.5" on a German locale).
//
// The reader is a small recursive-descent parser used only for inbound
// op envelopes (`{"topic":"...","op":"...","args":[...]}`). Output tree:
//   object  -> Dictionary<string, object>
//   array   -> List<object>
//   string  -> string
//   number  -> double        (integers too — handlers cast as needed)
//   bool    -> bool
//   null    -> null
// On any parse error it returns null; callers log and discard the frame.

using System.Collections.Generic;
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

        public static void WriteFloat(StringBuilder sb, float f)
        {
            // "R" on a float gives the shortest string that round-trips
            // back to the same float — considerably more compact than
            // casting to double and writing the full double-precision
            // form.
            sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
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

        // ---- Reader -------------------------------------------------

        /// <summary>
        /// Parse <paramref name="s"/> as a JSON value. Returns the
        /// rooted object tree (see file header for shape) or null on
        /// any syntax error or trailing garbage.
        /// </summary>
        public static object Parse(string s)
        {
            if (s == null) return null;
            int i = 0;
            if (!SkipWs(s, ref i)) return null;
            if (!TryParseValue(s, ref i, out object v)) return null;
            SkipWs(s, ref i);
            return i == s.Length ? v : null;
        }

        private static bool SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') i++;
                else return true;
            }
            return true;
        }

        private static bool TryParseValue(string s, ref int i, out object v)
        {
            v = null;
            if (!SkipWs(s, ref i) || i >= s.Length) return false;
            char c = s[i];
            switch (c)
            {
                case '{': return TryParseObject(s, ref i, out v);
                case '[': return TryParseArray(s, ref i, out v);
                case '"': return TryParseString(s, ref i, out v);
                case 't': case 'f': return TryParseBool(s, ref i, out v);
                case 'n': return TryParseNull(s, ref i, out v);
                default:
                    if (c == '-' || (c >= '0' && c <= '9'))
                        return TryParseNumber(s, ref i, out v);
                    return false;
            }
        }

        private static bool TryParseObject(string s, ref int i, out object v)
        {
            v = null;
            if (s[i] != '{') return false;
            i++;
            var dict = new Dictionary<string, object>();
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; v = dict; return true; }
            while (true)
            {
                SkipWs(s, ref i);
                if (!TryParseString(s, ref i, out object keyObj)) return false;
                string key = (string)keyObj;
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') return false;
                i++;
                if (!TryParseValue(s, ref i, out object val)) return false;
                dict[key] = val;
                SkipWs(s, ref i);
                if (i >= s.Length) return false;
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; v = dict; return true; }
                return false;
            }
        }

        private static bool TryParseArray(string s, ref int i, out object v)
        {
            v = null;
            if (s[i] != '[') return false;
            i++;
            var list = new List<object>();
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; v = list; return true; }
            while (true)
            {
                if (!TryParseValue(s, ref i, out object elem)) return false;
                list.Add(elem);
                SkipWs(s, ref i);
                if (i >= s.Length) return false;
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; v = list; return true; }
                return false;
            }
        }

        private static bool TryParseString(string s, ref int i, out object v)
        {
            v = null;
            if (i >= s.Length || s[i] != '"') return false;
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') { v = sb.ToString(); return true; }
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) return false;
                char esc = s[i++];
                switch (esc)
                {
                    case '"':  sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/':  sb.Append('/'); break;
                    case 'b':  sb.Append('\b'); break;
                    case 'f':  sb.Append('\f'); break;
                    case 'n':  sb.Append('\n'); break;
                    case 'r':  sb.Append('\r'); break;
                    case 't':  sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) return false;
                        if (!int.TryParse(s.Substring(i, 4),
                                NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture, out int cp))
                            return false;
                        sb.Append((char)cp);
                        i += 4;
                        break;
                    default: return false;
                }
            }
            return false;  // unterminated string
        }

        private static bool TryParseNumber(string s, ref int i, out object v)
        {
            v = null;
            int start = i;
            if (s[i] == '-') i++;
            while (i < s.Length)
            {
                char c = s[i];
                if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' ||
                    c == '+' || c == '-') i++;
                else break;
            }
            string slice = s.Substring(start, i - start);
            if (!double.TryParse(slice, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double d))
                return false;
            v = d;
            return true;
        }

        private static bool TryParseBool(string s, ref int i, out object v)
        {
            v = null;
            if (i + 4 <= s.Length && s[i] == 't' && s[i+1] == 'r' && s[i+2] == 'u' && s[i+3] == 'e')
            { i += 4; v = true; return true; }
            if (i + 5 <= s.Length && s[i] == 'f' && s[i+1] == 'a' && s[i+2] == 'l' && s[i+3] == 's' && s[i+4] == 'e')
            { i += 5; v = false; return true; }
            return false;
        }

        private static bool TryParseNull(string s, ref int i, out object v)
        {
            v = null;
            if (i + 4 <= s.Length && s[i] == 'n' && s[i+1] == 'u' && s[i+2] == 'l' && s[i+3] == 'l')
            { i += 4; return true; }
            return false;
        }
    }
}
