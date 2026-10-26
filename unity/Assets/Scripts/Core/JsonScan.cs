using System;

namespace Inspection
{
    /// <summary>
    /// 极小的 JSON 扫描工具：从一段 JSON 文本中取出顶层某个 key 对应的“原始值文本”。
    /// </summary>
    public static class JsonScan
    {
        /// <summary>
        /// 扫描字符串并返回第一个未转义的右引号下标（找不到返回 -1）。
        /// 用于把 JSON 字符串字面量安全地解包成普通字符串。
        /// </summary>
        public static int FindStringEnd(string json, int startQuoteIndex)
        {
            if (string.IsNullOrEmpty(json) || startQuoteIndex < 0 || startQuoteIndex >= json.Length ||
                json[startQuoteIndex] != '"')
            {
                return -1;
            }
            var escaped = false;
            for (var i = startQuoteIndex + 1; i < json.Length; i++)
            {
                if (escaped) { escaped = false; continue; }
                if (json[i] == '\\') { escaped = true; continue; }
                if (json[i] == '"') return i;
            }
            return -1;
        }

        /// <summary>
        /// 把 ValueOf 返回的“带引号字符串字面量”还原成普通字符串（处理 \" \\ \n \r \t \uXXXX）。
        /// 若输入不是带引号的字符串，则原样返回。
        /// </summary>
        public static string AsString(string jsonValue)
        {
            if (string.IsNullOrEmpty(jsonValue) || jsonValue[0] != '"')
            {
                return jsonValue;
            }
            var end = FindStringEnd(jsonValue, 0);
            if (end < 0)
            {
                return jsonValue;
            }

            var sb = new System.Text.StringBuilder(end);
            for (var i = 1; i < end; i++)
            {
                var c = jsonValue[i];
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                i++;
                if (i >= end) break;
                switch (jsonValue[i])
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 < end)
                        {
                            var hex = jsonValue.Substring(i + 1, 4);
                            if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var code))
                            {
                                sb.Append((char)code);
                                i += 4;
                            }
                        }
                        break;
                    default: sb.Append(jsonValue[i]); break; // \" \\ \/
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// 返回顶层 key 对应的原始值文本（含引号的字符串、完整对象、完整数组，或数字/true/false/null）。
        /// 找不到时返回 null。
        /// </summary>
        public static string ValueOf(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key))
            {
                return null;
            }

            var keyToken = "\"" + key + "\"";
            var searchFrom = 0;

            while (searchFrom < json.Length)
            {
                var idx = json.IndexOf(keyToken, searchFrom, StringComparison.Ordinal);
                if (idx < 0)
                {
                    return null;
                }

                if (!IsTopLevelKey(json, idx))
                {
                    searchFrom = idx + keyToken.Length;
                    continue;
                }

                // 跳过冒号与空白，定位值的起点
                var p = idx + keyToken.Length;
                while (p < json.Length && char.IsWhiteSpace(json[p])) p++;
                if (p >= json.Length || json[p] != ':')
                {
                    searchFrom = idx + keyToken.Length;
                    continue;
                }
                p++;
                while (p < json.Length && char.IsWhiteSpace(json[p])) p++;

                return ReadValue(json, p);
            }

            return null;
        }

        /// <summary>位置 idx 处的 key 是否位于根对象的直接成员位置（深度 1 且不在字符串内）。</summary>
        private static bool IsTopLevelKey(string json, int idx)
        {
            var depth = 0;
            var inString = false;
            var escaped = false;

            for (var i = 0; i < idx; i++)
            {
                var c = json[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
            }

            return depth == 1 && !inString;
        }

        /// <summary>从 start 开始读取一个完整 JSON 值，返回其原文。</summary>
        private static string ReadValue(string json, int start)
        {
            if (start >= json.Length)
            {
                return null;
            }

            var c = json[start];

            if (c == '"')
            {
                var escaped = false;
                for (var i = start + 1; i < json.Length; i++)
                {
                    if (escaped) { escaped = false; continue; }
                    if (json[i] == '\\') { escaped = true; continue; }
                    if (json[i] == '"') return json.Substring(start, i - start + 1);
                }
                return null; // 字符串没有闭合
            }

            if (c == '{' || c == '[')
            {
                var depth = 0;
                var inString = false;
                var escaped = false;
                for (var i = start; i < json.Length; i++)
                {
                    var ch = json[i];
                    if (inString)
                    {
                        if (escaped) escaped = false;
                        else if (ch == '\\') escaped = true;
                        else if (ch == '"') inString = false;
                        continue;
                    }
                    if (ch == '"') { inString = true; continue; }
                    if (ch == '{' || ch == '[') depth++;
                    else if (ch == '}' || ch == ']')
                    {
                        depth--;
                        if (depth == 0) return json.Substring(start, i - start + 1);
                    }
                }
                return null; // 括号没有闭合
            }

            // 数字 / true / false / null
            var end = start;
            while (end < json.Length && json[end] != ',' && json[end] != '}' && json[end] != ']' &&
                   !char.IsWhiteSpace(json[end]))
            {
                end++;
            }
            return end > start ? json.Substring(start, end - start) : null;
        }
    }
}
