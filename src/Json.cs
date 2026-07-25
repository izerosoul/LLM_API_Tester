using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

namespace ApiTester
{
    // JSON 工具：基于 .NET Framework 自带 JavaScriptSerializer，不依赖第三方 DLL。
    public static class JsonUtil
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        // 把对象序列化为紧凑 JSON；对象属性值为 null 时跳过，避免发送可空参数。
        public static string Serialize(object value)
        {
            return Serializer.Serialize(Clean(value));
        }

        // 美化 JSON 文本；若不是合法 JSON（或不完整）则原样返回。
        public static string Pretty(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return json;
            try
            {
                object root = Serializer.DeserializeObject(json);
                var sb = new StringBuilder();
                WritePretty(root, sb, 0);
                return sb.ToString();
            }
            catch
            {
                return json;
            }
        }

        public static object Parse(string json)
        {
            return Serializer.DeserializeObject(json)!;
        }

        public static bool TryParse(string json, out object element)
        {
            try { element = Parse(json); return true; }
            catch { element = null!; return false; }
        }

        public static string? GetString(object root, params string[] path)
        {
            object? cur;
            if (!Navigate(root, path, out cur)) return null;
            return cur as string;
        }

        public static int? GetInt(object root, params string[] path)
        {
            object? cur;
            if (!Navigate(root, path, out cur) || cur == null) return null;
            if (cur is int) return (int)cur;
            if (cur is long) return checked((int)(long)cur);
            if (cur is decimal) return checked((int)(decimal)cur);
            int parsed;
            return int.TryParse(Convert.ToString(cur, CultureInfo.InvariantCulture), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out parsed) ? parsed : (int?)null;
        }

        public static IDictionary<string, object>? AsObject(object value)
        {
            return value as IDictionary<string, object>;
        }

        public static object[]? AsArray(object value)
        {
            return value as object[];
        }

        private static bool Navigate(object root, string[] path, out object? result)
        {
            object? cur = root;
            foreach (string seg in path)
            {
                var dict = cur as IDictionary<string, object>;
                if (dict == null || !dict.TryGetValue(seg, out cur))
                {
                    result = null;
                    return false;
                }
            }
            result = cur;
            return true;
        }

        private static object? Clean(object? value)
        {
            if (value == null) return null;
            if (value is string || value.GetType().IsPrimitive || value is decimal) return value;

            var dict = value as IDictionary;
            if (dict != null)
            {
                var result = new Dictionary<string, object>();
                foreach (DictionaryEntry entry in dict)
                {
                    if (entry.Value == null) continue;
                    object? cleaned = Clean(entry.Value);
                    if (cleaned != null)
                        result[Convert.ToString(entry.Key, CultureInfo.InvariantCulture)] = cleaned;
                }
                return result;
            }

            var enumerable = value as IEnumerable;
            if (enumerable != null && !(value is string))
            {
                var list = new List<object?>();
                foreach (object item in enumerable) list.Add(Clean(item));
                return list;
            }

            var obj = new Dictionary<string, object>();
            foreach (PropertyInfo prop in value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!prop.CanRead) continue;
                object? propValue = prop.GetValue(value, null);
                if (propValue == null) continue;
                object? cleaned = Clean(propValue);
                if (cleaned != null) obj[prop.Name] = cleaned;
            }
            return obj;
        }

        private static void WritePretty(object? value, StringBuilder sb, int level)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }

            var dict = value as IDictionary<string, object>;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (var kv in dict)
                {
                    if (!first) sb.Append(',');
                    sb.AppendLine();
                    Indent(sb, level + 1);
                    sb.Append(Serializer.Serialize(kv.Key)).Append(": ");
                    WritePretty(kv.Value, sb, level + 1);
                    first = false;
                }
                if (!first) { sb.AppendLine(); Indent(sb, level); }
                sb.Append('}');
                return;
            }

            var arr = value as object[];
            if (arr != null)
            {
                sb.Append('[');
                for (int i = 0; i < arr.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.AppendLine();
                    Indent(sb, level + 1);
                    WritePretty(arr[i], sb, level + 1);
                }
                if (arr.Length > 0) { sb.AppendLine(); Indent(sb, level); }
                sb.Append(']');
                return;
            }

            sb.Append(Serializer.Serialize(value));
        }

        private static void Indent(StringBuilder sb, int level)
        {
            sb.Append(' ', level * 2);
        }
    }
}
