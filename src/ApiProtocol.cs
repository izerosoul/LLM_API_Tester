using System;
using System.Collections.Generic;

namespace ApiTester
{
    // 支持的 API 协议种类
    public enum ProtocolKind
    {
        OpenAiChat,       // OpenAI Chat Completions (/v1/chat/completions)
        Claude,           // Claude / Anthropic Messages (/v1/messages)
        Gemini,           // Google Gemini (generateContent)
        OpenAiResponses   // OpenAI Responses / Codex (/v1/responses)
    }

    // 一次 HTTP 请求的完整描述：既用于"发送前预览"，也用于实际执行
    public sealed class HttpRequestSpec
    {
        public string Method = "POST";                      // 请求方法 GET/POST
        public string Url = "";                             // 完整 URL（含 query）
        public Dictionary<string, string> Headers = new();  // 鉴权等请求头（不含 Content-Type）
        public string? Body;                                // 请求体；GET 时为 null
        public bool IsStream;                               // 是否 SSE 流式
    }

    // 用户在界面上配置的连接信息
    public sealed class ApiConfig
    {
        public ProtocolKind Kind;
        public string BaseUrl = "";
        public string ApiKey = "";
        public string Model = "";
    }

    public enum ProxyKind
    {
        None,
        Http,
        Socks5
    }

    // 请求代理配置；用户名密码均可为空。
    public sealed class ApiProxyConfig
    {
        public ProxyKind Kind = ProxyKind.None;
        public string Host = "";
        public int Port;
        public string User = "";
        public string Password = "";

        public bool Enabled => Kind != ProxyKind.None;
    }

    // 对话参数
    public sealed class ChatParams
    {
        public string Message = "Hello";   // 用户消息
        public string? System;             // system 提示（可空）
        public int MaxTokens = 1024;
        public double? Temperature;        // 可空：Claude 协议忽略
        public string? ReasoningEffort;    // 可空：OpenAI 推理模型使用
    }

    // 非流式对话结果
    public sealed class ChatResult
    {
        public string Text = "";           // 提取出的回复文本
        public int? PromptTokens;
        public int? CompletionTokens;
        public int? TotalTokens;
    }

    // 单个 SSE 事件块的解析结果
    public sealed class SseEvent
    {
        public string? TextDelta;          // 增量文本（可空）
        public bool IsDone;                // 是否结束
        public int? PromptTokens;
        public int? CompletionTokens;
        public int? TotalTokens;
    }

    // 协议适配器接口：把各家 API 的差异（端点/鉴权/请求体/响应体/SSE）收敛于此
    public interface IApiProtocol
    {
        ProtocolKind Kind { get; }
        string DisplayName { get; }
        string DefaultBaseUrl { get; }
        bool SupportsTemperature { get; }   // Claude=false

        HttpRequestSpec BuildListModels(ApiConfig cfg);
        List<string> ParseModelList(string body);

        HttpRequestSpec BuildChat(ApiConfig cfg, ChatParams p, bool stream);
        ChatResult ParseChatResponse(string body);
        SseEvent ParseSseEvent(string rawEventBlock);
    }

    // 协议工厂
    public static class ProtocolFactory
    {
        // 按种类创建适配器实例
        public static IApiProtocol Create(ProtocolKind kind)
        {
            switch (kind)
            {
                case ProtocolKind.OpenAiChat: return new OpenAiChatProtocol();
                case ProtocolKind.Claude: return new ClaudeProtocol();
                case ProtocolKind.Gemini: return new GeminiProtocol();
                case ProtocolKind.OpenAiResponses: return new OpenAiResponsesProtocol();
                default: return new OpenAiChatProtocol();
            }
        }

        // 全部适配器（用于填充协议下拉框）
        public static IApiProtocol[] All()
        {
            return new IApiProtocol[]
            {
                new OpenAiChatProtocol(),
                new ClaudeProtocol(),
                new GeminiProtocol(),
                new OpenAiResponsesProtocol()
            };
        }
    }

    // 协议实现共用的小工具
    internal static class ProtocolUtil
    {
        // 去掉 Base URL 末尾的斜杠
        public static string TrimBase(string baseUrl)
        {
            return (baseUrl ?? "").TrimEnd('/');
        }

        // 拼接端点；只要 Base URL 以 /vN 结尾（v 后带数字，如 /v1、/v1beta），
        // 就认为版本已包含在 Base URL 中：去掉端点开头的版本段，只拼接后面的路径，
        // 并保证斜杠不重不漏。
        public static string BuildUrl(string baseUrl, string endpoint)
        {
            string b = TrimBase(baseUrl);
            string path = endpoint.StartsWith("/") ? endpoint : "/" + endpoint;
            if (TryGetLeadingVersionSegment(path, out string versionSegment)
                && BaseEndsWithVersion(b))
            {
                path = path.Substring(versionSegment.Length + 1);
                if (path.Length == 0) path = "/";
            }
            return b + path;
        }

        // 判断一段路径片段是否是版本段（v/V 开头且后面紧跟数字）
        private static bool IsVersionSegment(string segment)
        {
            if (segment.Length < 2) return false;
            if (segment[0] != 'v' && segment[0] != 'V') return false;
            return char.IsDigit(segment[1]);
        }

        private static bool TryGetLeadingVersionSegment(string path, out string versionSegment)
        {
            versionSegment = "";
            string value = path.StartsWith("/") ? path.Substring(1) : path;
            int slash = value.IndexOf('/');
            string firstSegment = slash >= 0 ? value.Substring(0, slash) : value;
            if (!IsVersionSegment(firstSegment)) return false;

            versionSegment = firstSegment;
            return true;
        }

        // 判断 Base URL 末尾是否已经带有 /vN 版本段（忽略 query/fragment 与末尾斜杠）
        private static bool BaseEndsWithVersion(string baseUrl)
        {
            string value = baseUrl;
            int query = value.IndexOfAny(new[] { '?', '#' });
            if (query >= 0) value = value.Substring(0, query);
            value = value.TrimEnd('/');

            int slash = value.LastIndexOf('/');
            if (slash < 0) return false;
            return IsVersionSegment(value.Substring(slash + 1));
        }
    }
}
