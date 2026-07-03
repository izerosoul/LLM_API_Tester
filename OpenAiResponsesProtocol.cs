using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace ApiTester
{
    // OpenAI Responses（Codex 使用的接口）协议适配器
    public sealed class OpenAiResponsesProtocol : IApiProtocol
    {
        public ProtocolKind Kind => ProtocolKind.OpenAiResponses;
        public string DisplayName => "OpenAI Responses (Codex)";
        public string DefaultBaseUrl => "https://api.openai.com";
        public bool SupportsTemperature => true;

        public HttpRequestSpec BuildListModels(ApiConfig cfg)
        {
            string b = ProtocolUtil.TrimBase(cfg.BaseUrl);
            var spec = new HttpRequestSpec { Method = "GET", Url = b + "/v1/models" };
            spec.Headers["Authorization"] = "Bearer " + cfg.ApiKey;
            return spec;
        }

        public List<string> ParseModelList(string body)
        {
            // 与 OpenAI 列模型结构相同
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
            var body = new
            {
                model = cfg.Model,
                input = p.Message,
                instructions = string.IsNullOrWhiteSpace(p.System) ? null : p.System,
                max_output_tokens = p.MaxTokens,
                temperature = p.Temperature,
                stream = stream
            };
            var spec = new HttpRequestSpec
            {
                Method = "POST",
                Url = b + "/v1/responses",
                Body = JsonUtil.Serialize(body),
                IsStream = stream
            };
            spec.Headers["Authorization"] = "Bearer " + cfg.ApiKey;
            return spec;
        }

        public ChatResult ParseChatResponse(string body)
        {
            var r = new ChatResult();
            JsonElement root = JsonUtil.Parse(body);

            // 优先便捷字段 output_text；否则从 output[] 中 type=message 的 content[].text 拼接
            string? convenience = JsonUtil.GetString(root, "output_text");
            if (!string.IsNullOrEmpty(convenience))
            {
                r.Text = convenience!;
            }
            else if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var item in output.EnumerateArray())
                {
                    if (JsonUtil.GetString(item, "type") == "message"
                        && item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var c in content.EnumerateArray())
                        {
                            if (c.TryGetProperty("text", out var te) && te.ValueKind == JsonValueKind.String)
                                sb.Append(te.GetString());
                        }
                    }
                }
                r.Text = sb.ToString();
            }

            r.PromptTokens = JsonUtil.GetInt(root, "usage", "input_tokens");
            r.CompletionTokens = JsonUtil.GetInt(root, "usage", "output_tokens");
            r.TotalTokens = JsonUtil.GetInt(root, "usage", "total_tokens");
            return r;
        }

        public SseEvent ParseSseEvent(string rawEventBlock)
        {
            var ev = new SseEvent();
            string? data = SseUtil.ExtractData(rawEventBlock);
            if (data == null) return ev;
            if (data.Trim() == "[DONE]") { ev.IsDone = true; return ev; }
            if (!JsonUtil.TryParse(data, out var root)) return ev;

            switch (JsonUtil.GetString(root, "type"))
            {
                case "response.output_text.delta":
                    ev.TextDelta = JsonUtil.GetString(root, "delta");
                    break;
                case "response.completed":
                    ev.IsDone = true;
                    ev.PromptTokens = JsonUtil.GetInt(root, "response", "usage", "input_tokens");
                    ev.CompletionTokens = JsonUtil.GetInt(root, "response", "usage", "output_tokens");
                    ev.TotalTokens = JsonUtil.GetInt(root, "response", "usage", "total_tokens");
                    break;
            }
            return ev;
        }
    }
}
