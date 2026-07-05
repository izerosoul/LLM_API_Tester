using System.Collections.Generic;
using System.Text;

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
            var spec = new HttpRequestSpec { Method = "GET", Url = ProtocolUtil.BuildUrl(cfg.BaseUrl, "/v1/models") };
            spec.Headers["Authorization"] = "Bearer " + cfg.ApiKey;
            return spec;
        }

        public List<string> ParseModelList(string body)
        {
            // 与 OpenAI 列模型结构相同
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
            var body = new
            {
                model = cfg.Model,
                input = p.Message,
                instructions = string.IsNullOrWhiteSpace(p.System) ? null : p.System,
                max_output_tokens = p.MaxTokens,
                temperature = p.Temperature,
                reasoning = string.IsNullOrWhiteSpace(p.ReasoningEffort) ? null : new { effort = p.ReasoningEffort },
                stream = stream
            };
            var spec = new HttpRequestSpec
            {
                Method = "POST",
                Url = ProtocolUtil.BuildUrl(cfg.BaseUrl, "/v1/responses"),
                Body = JsonUtil.Serialize(body),
                IsStream = stream
            };
            spec.Headers["Authorization"] = "Bearer " + cfg.ApiKey;
            return spec;
        }

        public ChatResult ParseChatResponse(string body)
        {
            var r = new ChatResult();
            object root = JsonUtil.Parse(body);

            // 优先便捷字段 output_text；否则从 output[] 中 type=message 的 content[].text 拼接
            string? convenience = JsonUtil.GetString(root, "output_text");
            if (!string.IsNullOrEmpty(convenience))
            {
                r.Text = convenience!;
            }
            else
            {
                var dict = JsonUtil.AsObject(root);
                object outputObj;
                var output = dict != null && dict.TryGetValue("output", out outputObj) ? JsonUtil.AsArray(outputObj) : null;
                if (output != null)
                {
                    var sb = new StringBuilder();
                    foreach (object item in output)
                    {
                        var itemDict = JsonUtil.AsObject(item);
                        object contentObj;
                        var content = itemDict != null && itemDict.TryGetValue("content", out contentObj)
                            ? JsonUtil.AsArray(contentObj)
                            : null;
                        if (JsonUtil.GetString(item, "type") == "message" && content != null)
                        {
                            foreach (object c in content)
                            {
                                string? text = JsonUtil.GetString(c, "text");
                                if (text != null) sb.Append(text);
                            }
                        }
                    }
                    r.Text = sb.ToString();
                }
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
            object root;
            if (!JsonUtil.TryParse(data, out root)) return ev;

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
