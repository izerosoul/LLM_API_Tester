using System;
using System.Collections.Generic;
using System.Text;

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
            object root = JsonUtil.Parse(body);
            var dict = JsonUtil.AsObject(root);
            object modelsObj;
            var models = dict != null && dict.TryGetValue("models", out modelsObj) ? JsonUtil.AsArray(modelsObj) : null;
            if (models != null)
            {
                foreach (object m in models)
                {
                    string? name = JsonUtil.GetString(m, "name");
                    if (name != null) list.Add(name);   // 形如 "models/gemini-..."
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
        private static string ExtractText(object root)
        {
            var sb = new StringBuilder();
            var dict = JsonUtil.AsObject(root);
            object candsObj;
            var cands = dict != null && dict.TryGetValue("candidates", out candsObj) ? JsonUtil.AsArray(candsObj) : null;
            if (cands != null && cands.Length > 0)
            {
                var first = cands[0];
                var content = JsonUtil.AsObject(first);
                object contentObj;
                object partsObj;
                object[]? parts = null;
                if (content != null && content.TryGetValue("content", out contentObj))
                {
                    var contentDict = JsonUtil.AsObject(contentObj);
                    if (contentDict != null && contentDict.TryGetValue("parts", out partsObj))
                        parts = JsonUtil.AsArray(partsObj);
                }
                if (parts != null)
                {
                    foreach (object part in parts)
                    {
                        string? text = JsonUtil.GetString(part, "text");
                        if (text != null) sb.Append(text);
                    }
                }
            }
            return sb.ToString();
        }

        public ChatResult ParseChatResponse(string body)
        {
            var r = new ChatResult();
            object root = JsonUtil.Parse(body);
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
            object root;
            if (!JsonUtil.TryParse(data, out root)) return ev;

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
