using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Localsend.Backend.Util;

namespace Localsend.Backend.Protocol
{
    /// <summary>
    /// 极简 JSON 读写器。仅覆盖 LocalSend 所需子集。
    /// - 对象：Dictionary&lt;string, object&gt;
    /// - 数组：List&lt;object&gt;
    /// - 字符串：string
    /// - 数字：long（整数）或 double（小数）
    /// - 布尔：bool
    /// - null：null
    /// </summary>
    internal static class Json
    {
        public static object Parse(string s)
        {
            if (s == null) throw new ArgumentNullException("s");
            int i = 0;
            SkipWs(s, ref i);
            object v = ReadValue(s, ref i);
            SkipWs(s, ref i);
            if (i != s.Length) throw new FormatException("JSON: trailing data at " + i);
            return v;
        }

        public static Dictionary<string, object> ParseObject(string s)
        {
            Dictionary<string, object> o = Parse(s) as Dictionary<string, object>;
            if (o == null) throw new FormatException("JSON: root is not an object");
            return o;
        }

        public static string Stringify(object v)
        {
            StringBuilder sb = new StringBuilder();
            WriteValue(sb, v);
            return sb.ToString();
        }

        // ---- reader ----

        private static object ReadValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("JSON: unexpected EOF");
            char c = s[i];
            if (c == '{') return ReadObject(s, ref i);
            if (c == '[') return ReadArray(s, ref i);
            if (c == '"') return ReadString(s, ref i);
            if (c == 't' || c == 'f') return ReadBool(s, ref i);
            if (c == 'n') { ReadLiteral(s, ref i, "null"); return null; }
            if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber(s, ref i);
            throw new FormatException("JSON: unexpected char '" + c + "' at " + i);
        }

        private static Dictionary<string, object> ReadObject(string s, ref int i)
        {
            Dictionary<string, object> o = new Dictionary<string, object>();
            i++; // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return o; }
            while (true)
            {
                SkipWs(s, ref i);
                string key = ReadString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("JSON: expected ':' at " + i);
                i++;
                object val = ReadValue(s, ref i);
                o[key] = val;
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("JSON: unterminated object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return o; }
                throw new FormatException("JSON: expected ',' or '}' at " + i);
            }
        }

        private static List<object> ReadArray(string s, ref int i)
        {
            List<object> a = new List<object>();
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return a; }
            while (true)
            {
                a.Add(ReadValue(s, ref i));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("JSON: unterminated array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return a; }
                throw new FormatException("JSON: expected ',' or ']' at " + i);
            }
        }

        private static string ReadString(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("JSON: expected string at " + i);
            i++;
            StringBuilder sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    if (i >= s.Length) break;
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 > s.Length) throw new FormatException("JSON: bad \\u");
                            int cp = int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                            i += 4;
                            sb.Append((char)cp);
                            break;
                        default: throw new FormatException("JSON: bad escape \\" + e);
                    }
                }
                else sb.Append(c);
            }
            throw new FormatException("JSON: unterminated string");
        }

        private static object ReadNumber(string s, ref int i)
        {
            int start = i;
            if (s[i] == '-') i++;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
            bool isFloat = false;
            if (i < s.Length && s[i] == '.')
            {
                isFloat = true; i++;
                while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
            }
            if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
            {
                isFloat = true; i++;
                if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
                while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
            }
            string tok = s.Substring(start, i - start);
            if (isFloat) return double.Parse(tok, CultureInfo.InvariantCulture);
            long l;
            if (Localsend.Backend.Util.Parse.TryLong(tok, out l)) return l;
            return double.Parse(tok, CultureInfo.InvariantCulture);
        }

        private static bool ReadBool(string s, ref int i)
        {
            if (s[i] == 't') { ReadLiteral(s, ref i, "true"); return true; }
            ReadLiteral(s, ref i, "false"); return false;
        }

        private static void ReadLiteral(string s, ref int i, string lit)
        {
            if (i + lit.Length > s.Length || s.Substring(i, lit.Length) != lit)
                throw new FormatException("JSON: expected " + lit + " at " + i);
            i += lit.Length;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') i++;
                else break;
            }
        }

        // ---- writer ----

        private static void WriteValue(StringBuilder sb, object v)
        {
            if (v == null) { sb.Append("null"); return; }
            if (v is bool) { sb.Append(((bool)v) ? "true" : "false"); return; }
            if (v is string) { WriteString(sb, (string)v); return; }
            if (v is long || v is int || v is short || v is byte)
            { sb.Append(Convert.ToInt64(v).ToString(CultureInfo.InvariantCulture)); return; }
            if (v is double || v is float || v is decimal)
            { sb.Append(Convert.ToDouble(v).ToString("R", CultureInfo.InvariantCulture)); return; }

            Dictionary<string, object> o = v as Dictionary<string, object>;
            if (o != null) { WriteObject(sb, o); return; }
            List<object> a = v as List<object>;
            if (a != null) { WriteArray(sb, a); return; }

            // fallback: treat as string
            WriteString(sb, v.ToString());
        }

        private static void WriteObject(StringBuilder sb, Dictionary<string, object> o)
        {
            sb.Append('{');
            bool first = true;
            foreach (KeyValuePair<string, object> kv in o)
            {
                if (!first) sb.Append(',');
                first = false;
                WriteString(sb, kv.Key);
                sb.Append(':');
                WriteValue(sb, kv.Value);
            }
            sb.Append('}');
        }

        private static void WriteArray(StringBuilder sb, List<object> a)
        {
            sb.Append('[');
            for (int i = 0; i < a.Count; i++)
            {
                if (i > 0) sb.Append(',');
                WriteValue(sb, a[i]);
            }
            sb.Append(']');
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
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
    }
}
