using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace ApiTester
{
    // Claude / Anthropic Messages 协议适配器
    public sealed class ClaudeProtocol : IApiProtocol
    {
        public ProtocolKind Kind => ProtocolKind.Claude;
        public string DisplayName => "Claude / Anthropic";
        public string DefaultBaseUrl => "https://api.anthropic.com";
        public bool SupportsTemperature => false;   // Opus 4.7/4.8 不接受 temperature

        // 统一加上 Anthropic 鉴权头
        private static void AddAuth(HttpRequestSpec spec, ApiConfig cfg)
        {
            spec.Headers["x-api-key"] = cfg.ApiKey;
            spec.Headers["anthropic-version"] = "2023-06-01";
        }

        public HttpRequestSpec BuildListModels(ApiConfig cfg)
        {
            string b = ProtocolUtil.TrimBase(cfg.BaseUrl);
            var spec = new HttpRequestSpec { Method = "GET", Url = b + "/v1/models" };
            AddAuth(spec, cfg);
            return spec;
        }

        public List<string> ParseModelList(string body)
        {
            var list = new List<string>();
            JsonElement root = JsonUtil.Parse(body);
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        list.Add(id.GetString()!);
                }
            }
            list.Sort();
            return list;
        }

        public HttpRequestSpec BuildChat(ApiConfig cfg, ChatParams p, bool stream)
        {
            string b = ProtocolUtil.TrimBase(cfg.BaseUrl);
            var messages = new List<object> { new { role = "user", content = p.Message } };
            var body = new
            {
                model = cfg.Model,
                max_tokens = p.MaxTokens,                                  // Anthropic 必填
                system = string.IsNullOrWhiteSpace(p.System) ? null : p.System,
                messages = messages,
                stream = stream
                // 刻意不发 temperature
            };
            var spec = new HttpRequestSpec
            {
                Method = "POST",
                Url = b + "/v1/messages",
                Body = JsonUtil.Serialize(body),
                IsStream = stream
            };
            AddAuth(spec, cfg);
            return spec;
        }

        public ChatResult ParseChatResponse(string body)
        {
            var r = new ChatResult();
            JsonElement root = JsonUtil.Parse(body);
            if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var blk in content.EnumerateArray())
                {
                    if (JsonUtil.GetString(blk, "type") == "text")
                    {
                        string? t = JsonUtil.GetString(blk, "text");
                        if (t != null) sb.Append(t);
                    }
                }
                r.Text = sb.ToString();
            }
            r.PromptTokens = JsonUtil.GetInt(root, "usage", "input_tokens");
            r.CompletionTokens = JsonUtil.GetInt(root, "usage", "output_tokens");
            if (r.PromptTokens.HasValue || r.CompletionTokens.HasValue)
                r.TotalTokens = (r.PromptTokens ?? 0) + (r.CompletionTokens ?? 0);
            return r;
        }

        public SseEvent ParseSseEvent(string rawEventBlock)
        {
            var ev = new SseEvent();
            string? data = SseUtil.ExtractData(rawEventBlock);
            if (data == null) return ev;
            if (!JsonUtil.TryParse(data, out var root)) return ev;

            switch (JsonUtil.GetString(root, "type"))
            {
                case "content_block_delta":
                    ev.TextDelta = JsonUtil.GetString(root, "delta", "text");
                    break;
                case "message_start":
                    ev.PromptTokens = JsonUtil.GetInt(root, "message", "usage", "input_tokens");
                    break;
                case "message_delta":
                    ev.CompletionTokens = JsonUtil.GetInt(root, "usage", "output_tokens");
                    break;
                case "message_stop":
                    ev.IsDone = true;
                    break;
            }
            return ev;
        }
    }
}
