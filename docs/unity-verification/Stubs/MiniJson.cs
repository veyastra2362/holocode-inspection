// 极简 JsonUtility 实现（仅覆盖本项目用到的字段类型），用于离线验证序列化往返。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace UnityEngine
{
    public static class MiniJson
    {
        public static string Serialize(object obj)
        {
            var sb = new StringBuilder();
            WriteValue(sb, obj);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object v)
        {
            switch (v)
            {
                case null: sb.Append("null"); return;
                case string s: WriteString(sb, s); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case float f: sb.Append(f.ToString("R", CultureInfo.InvariantCulture)); return;
                case double d: sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); return;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); return;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); return;
                case IDictionary dict:
                    sb.Append('{');
                    var first = true;
                    foreach (DictionaryEntry e in dict)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, e.Key.ToString());
                        sb.Append(':');
                        WriteValue(sb, e.Value);
                    }
                    sb.Append('}');
                    return;
                case IEnumerable en when !(v is string):
                    sb.Append('[');
                    var f2 = true;
                    foreach (var item in en)
                    {
                        if (!f2) sb.Append(',');
                        f2 = false;
                        WriteValue(sb, item);
                    }
                    sb.Append(']');
                    return;
                default:
                    // 普通对象：按字段序列化（与 JsonUtility 一致）
                    var t = v.GetType();
                    sb.Append('{');
                    var firstField = true;
                    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (!firstField) sb.Append(',');
                        firstField = false;
                        WriteString(sb, f.Name);
                        sb.Append(':');
                        WriteValue(sb, f.GetValue(v));
                    }
                    sb.Append('}');
                    return;
            }
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append('"');
        }

        public static object Deserialize(Type type, string json)
        {
            var node = Parse(json);
            return Convert(node, type);
        }

        private static object Node(string json) => Parse(json);

        // --- 解析 ---
        private static object Parse(string json)
        {
            var i = 0;
            var v = ParseValue(json, ref i);
            return v;
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) return null;
            var c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return ParseString(s, ref i);
            if (s.Substring(i).StartsWith("true")) { i += 4; return true; }
            if (s.Substring(i).StartsWith("false")) { i += 5; return false; }
            if (s.Substring(i).StartsWith("null")) { i += 4; return null; }
            var start = i;
            while (i < s.Length && ",}] \t\r\n".IndexOf(s[i]) < 0) i++;
            var num = s.Substring(start, i - start);
            return double.Parse(num, CultureInfo.InvariantCulture);
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var d = new Dictionary<string, object>();
            i++; // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (i < s.Length)
            {
                SkipWs(s, ref i);
                var key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (s[i] == ':') i++;
                var val = ParseValue(s, ref i);
                d[key] = val;
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; break; }
                break;
            }
            return d;
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var list = new List<object>();
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }
            while (i < s.Length)
            {
                list.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; break; }
                break;
            }
            return list;
        }

        private static string ParseString(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (s[i] != '"') return null;
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                var c = s[i++];
                if (c == '"') break;
                if (c == '\\' && i < s.Length)
                {
                    var e = s[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            var hex = s.Substring(i, 4);
                            i += 4;
                            sb.Append((char)System.Convert.ToInt32(hex, 16));
                            break;
                        default: sb.Append(e); break;
                    }
                    continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        // --- 映射到类型 ---
        private static object Convert(object node, Type type)
        {
            if (type == typeof(string)) return node as string ?? (node == null ? null : node.ToString());
            if (type == typeof(bool)) return node is bool b ? b : false;
            if (type == typeof(float)) return node == null ? 0f : (float)System.Convert.ToDouble(node, CultureInfo.InvariantCulture);
            if (type == typeof(double)) return node == null ? 0d : System.Convert.ToDouble(node, CultureInfo.InvariantCulture);
            if (type == typeof(int)) return node == null ? 0 : (int)System.Convert.ToDouble(node, CultureInfo.InvariantCulture);
            if (type.IsArray)
            {
                var elem = type.GetElementType();
                var list = node as List<object> ?? new List<object>();
                var arr = Array.CreateInstance(elem, list.Count);
                for (var k = 0; k < list.Count; k++) arr.SetValue(Convert(list[k], elem), k);
                return arr;
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                var elem = type.GetGenericArguments()[0];
                var list = (IList)Activator.CreateInstance(type);
                foreach (var item in node as List<object> ?? new List<object>()) list.Add(Convert(item, elem));
                return list;
            }
            if (type.IsClass)
            {
                var obj = Activator.CreateInstance(type);
                if (node is Dictionary<string, object> dict)
                {
                    foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (dict.TryGetValue(f.Name, out var v)) f.SetValue(obj, Convert(v, f.FieldType));
                    }
                }
                return obj;
            }
            return node;
        }
    }
}

