using System.Collections.Generic;

namespace ApiTester
{
    // OpenAI Chat Completions 协议适配器
    public sealed class OpenAiChatProtocol : IApiProtocol
    {
        public ProtocolKind Kind => ProtocolKind.OpenAiChat;
        public string DisplayName => "OpenAI Chat Completions";
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
            string b = ProtocolUtil.TrimBase(cfg.BaseUrl);
            var messages = new List<object>();
            if (!string.IsNullOrWhiteSpace(p.System))
                messages.Add(new { role = "system", content = p.System });
            messages.Add(new { role = "user", content = p.Message });

            var body = new
            {
                model = cfg.Model,
                messages = messages,
                max_tokens = p.MaxTokens,
                temperature = p.Temperature,   // 为 null 时序列化阶段被忽略
                stream = stream
            };
            var spec = new HttpRequestSpec
            {
                Method = "POST",
                Url = b + "/v1/chat/completions",
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
            var dict = JsonUtil.AsObject(root);
            object choicesObj;
            var choices = dict != null && dict.TryGetValue("choices", out choicesObj) ? JsonUtil.AsArray(choicesObj) : null;
            if (choices != null && choices.Length > 0)
            {
                r.Text = JsonUtil.GetString(choices[0], "message", "content") ?? "";
            }
            r.PromptTokens = JsonUtil.GetInt(root, "usage", "prompt_tokens");
            r.CompletionTokens = JsonUtil.GetInt(root, "usage", "completion_tokens");
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

            var dict = JsonUtil.AsObject(root);
            object choicesObj;
            var choices = dict != null && dict.TryGetValue("choices", out choicesObj) ? JsonUtil.AsArray(choicesObj) : null;
            if (choices != null && choices.Length > 0)
            {
                ev.TextDelta = JsonUtil.GetString(choices[0], "delta", "content");
            }
            // include_usage 时末尾块带 usage
            ev.PromptTokens = JsonUtil.GetInt(root, "usage", "prompt_tokens");
            ev.CompletionTokens = JsonUtil.GetInt(root, "usage", "completion_tokens");
            ev.TotalTokens = JsonUtil.GetInt(root, "usage", "total_tokens");
            return ev;
        }
    }
}
