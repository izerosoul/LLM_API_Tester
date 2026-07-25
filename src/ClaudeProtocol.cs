using System.Collections.Generic;
using System.Text;

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
            var spec = new HttpRequestSpec { Method = "GET", Url = ProtocolUtil.BuildUrl(cfg.BaseUrl, "/v1/models") };
            AddAuth(spec, cfg);
            return spec;
        }

        public List<string> ParseModelList(string body)
        {
            var list = new List<string>();
            object root = JsonUtil.Parse(body);
            var dict = JsonUtil.AsObject(root);
            object dataObj;
            var data = dict != null && dict.TryGetValue("data", out dataObj) ? JsonUtil.AsArray(dataObj) : null;
            if (data != null)
            {
                foreach (object item in data)
                {
                    string? id = JsonUtil.GetString(item, "id");
                    if (id != null) list.Add(id);
                }
            }
            list.Sort();
            return list;
        }

        public HttpRequestSpec BuildChat(ApiConfig cfg, ChatParams p, bool stream)
        {
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
                Url = ProtocolUtil.BuildUrl(cfg.BaseUrl, "/v1/messages"),
                Body = JsonUtil.Serialize(body),
                IsStream = stream
            };
            AddAuth(spec, cfg);
            return spec;
        }

        public ChatResult ParseChatResponse(string body)
        {
            var r = new ChatResult();
            object root = JsonUtil.Parse(body);
            var dict = JsonUtil.AsObject(root);
            object contentObj;
            var content = dict != null && dict.TryGetValue("content", out contentObj) ? JsonUtil.AsArray(contentObj) : null;
            if (content != null)
            {
                var sb = new StringBuilder();
                foreach (object blk in content)
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
            object root;
            if (!JsonUtil.TryParse(data, out root)) return ev;

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
