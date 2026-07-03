using System;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ApiTester
{
    // JSON 工具：序列化请求体、美化响应、基于 JsonElement 的安全取值
    public static class JsonUtil
    {
        // 美化输出：缩进 + 不转义非 ASCII（中文正常显示）
        private static readonly JsonSerializerOptions PrettyOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        // 紧凑输出：忽略值为 null 的字段（这样可空参数不发送，如 temperature/system）
        private static readonly JsonSerializerOptions CompactOptions = new()
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        // 把对象序列化为紧凑 JSON（用于构造请求体）
        public static string Serialize(object value)
        {
            return JsonSerializer.Serialize(value, CompactOptions);
        }

        // 美化 JSON 文本；若不是合法 JSON（或不完整）则原样返回
        public static string Pretty(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return json;
            try
            {
                JsonNode? node = JsonNode.Parse(json);
                return node == null ? json : node.ToJsonString(PrettyOptions);
            }
            catch
            {
                return json;
            }
        }

        // 解析为可独立保存的 JsonElement（doc 释放后仍可用）
        public static JsonElement Parse(string json)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }

        public static bool TryParse(string json, out JsonElement element)
        {
            try { element = Parse(json); return true; }
            catch { element = default; return false; }
        }

        // 路径式取字符串：依次下钻对象属性；任一步失败返回 null
        public static string? GetString(JsonElement root, params string[] path)
        {
            if (!Navigate(root, path, out JsonElement cur)) return null;
            return cur.ValueKind == JsonValueKind.String ? cur.GetString() : null;
        }

        // 路径式取整数
        public static int? GetInt(JsonElement root, params string[] path)
        {
            if (!Navigate(root, path, out JsonElement cur)) return null;
            if (cur.ValueKind == JsonValueKind.Number && cur.TryGetInt32(out int v)) return v;
            return null;
        }

        // 沿对象属性链下钻
        private static bool Navigate(JsonElement root, string[] path, out JsonElement result)
        {
            JsonElement cur = root;
            foreach (string seg in path)
            {
                if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(seg, out cur))
                {
                    result = default;
                    return false;
                }
            }
            result = cur;
            return true;
        }
    }
}
