using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace ApiTester
{
    // Google Gemini 协议适配器
    public sealed class GeminiProtocol : IApiProtocol
    {
        public ProtocolKind Kind => ProtocolKind.Gemini;
        public string DisplayName => "Google Gemini";
        public string DefaultBaseUrl => "https://generativelanguage.googleapis.com";
        public bool SupportsTemperature => true;

        // 去掉模型名可能带的 "models/" 前缀（请求 URL 路径里已含 models/）
        private static string ModelId(string model)
            => model.StartsWith("models/") ? model.Substring("models/".Length) : model;

        public HttpRequestSpec BuildListModels(ApiConfig cfg)
        {
            string b = ProtocolUtil.TrimBase(cfg.BaseUrl);
            var spec = new HttpRequestSpec
            {
                Method = "GET",
                Url = b + "/v1beta/models?key=" + Uri.EscapeDataString(cfg.ApiKey)
            };
            spec.Headers["x-goog-api-key"] = cfg.ApiKey;
            return spec;
        }

        public List<string> ParseModelList(string body)
        {
            var list = new List<string>();
            JsonElement root = JsonUtil.Parse(body);
            if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in models.EnumerateArray())
                {
                    if (m.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                        list.Add(n.GetString()!);   // 形如 "models/gemini-..."
                }
            }
            list.Sort();
            return list;
        }

        public HttpRequestSpec BuildChat(ApiConfig cfg, ChatParams p, bool stream)
        {
            string b = ProtocolUtil.TrimBase(cfg.BaseUrl);
            string model = ModelId(cfg.Model);
            string verb = stream ? "streamGenerateContent" : "generateContent";
            string url = b + "/v1beta/models/" + model + ":" + verb + "?key=" + Uri.EscapeDataString(cfg.ApiKey);
            if (stream) url += "&alt=sse";

            var contents = new List<object>
            {
                new { role = "user", parts = new object[] { new { text = p.Message } } }
            };
            object? sysInstr = string.IsNullOrWhiteSpace(p.System)
                ? null
                : new { parts = new object[] { new { text = p.System } } };

            var body = new
            {
                contents = contents,
                systemInstruction = sysInstr,
                generationConfig = new { maxOutputTokens = p.MaxTokens, temperature = p.Temperature }
            };
            var spec = new HttpRequestSpec
            {
                Method = "POST",
                Url = url,
                Body = JsonUtil.Serialize(body),
                IsStream = stream
            };
            spec.Headers["x-goog-api-key"] = cfg.ApiKey;
            return spec;
        }

        // 从 candidates[0].content.parts[*].text 拼接文本
        private static string ExtractText(JsonElement root)
        {
            var sb = new StringBuilder();
            if (root.TryGetProperty("candidates", out var cands)
                && cands.ValueKind == JsonValueKind.Array && cands.GetArrayLength() > 0)
            {
                var first = cands[0];
                if (first.TryGetProperty("content", out var content)
                    && content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var te) && te.ValueKind == JsonValueKind.String)
                            sb.Append(te.GetString());
                    }
                }
            }
            return sb.ToString();
        }

        public ChatResult ParseChatResponse(string body)
        {
            var r = new ChatResult();
            JsonElement root = JsonUtil.Parse(body);
            r.Text = ExtractText(root);
            r.PromptTokens = JsonUtil.GetInt(root, "usageMetadata", "promptTokenCount");
            r.CompletionTokens = JsonUtil.GetInt(root, "usageMetadata", "candidatesTokenCount");
            r.TotalTokens = JsonUtil.GetInt(root, "usageMetadata", "totalTokenCount");
            return r;
        }

        public SseEvent ParseSseEvent(string rawEventBlock)
        {
            var ev = new SseEvent();
            string? data = SseUtil.ExtractData(rawEventBlock);
            if (data == null) return ev;
            if (!JsonUtil.TryParse(data, out var root)) return ev;

            string t = ExtractText(root);
            if (t.Length > 0) ev.TextDelta = t;
            // 每个流式块带累计 usageMetadata
            ev.PromptTokens = JsonUtil.GetInt(root, "usageMetadata", "promptTokenCount");
            ev.CompletionTokens = JsonUtil.GetInt(root, "usageMetadata", "candidatesTokenCount");
            ev.TotalTokens = JsonUtil.GetInt(root, "usageMetadata", "totalTokenCount");
            return ev;
        }
    }
}
