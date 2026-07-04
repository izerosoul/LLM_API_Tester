using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ApiTester
{
    // HTTP 执行结果
    public sealed class HttpResult
    {
        public int Status;
        public string StatusText = "";
        public string Body = "";
        public long ElapsedMs;
        public long TtftMs;                 // 首字节耗时（非流式 = 总耗时）
        public Exception? Error;
        public Dictionary<string, string> Headers = new();
        // 流式时由 ApiClient 边解析边累积；非流式由调用方解析 Body 获得
        public int? PromptTokens;
        public int? CompletionTokens;
        public int? TotalTokens;

        public bool IsSuccess => Status >= 200 && Status < 300;
        public bool Cancelled => Error is OperationCanceledException;
    }

    // 基于 HttpClient 的执行层：非流式 + SSE 流式
    public sealed class ApiClient
    {
        // 单实例复用
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            // .NET Framework 默认协议在旧系统上可能偏保守，这里显式启用 TLS 1.2。
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var c = new HttpClient();
            c.Timeout = TimeSpan.FromMinutes(5);
            return c;
        }

        private static HttpRequestMessage BuildRequest(HttpRequestSpec spec)
        {
            var req = new HttpRequestMessage(new HttpMethod(spec.Method), spec.Url);
            if (spec.Body != null)
                req.Content = new StringContent(spec.Body, Encoding.UTF8, "application/json");
            foreach (var kv in spec.Headers)
                req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            return req;
        }

        private static void CopyHeaders(HttpResponseMessage resp, HttpResult result)
        {
            foreach (var h in resp.Headers)
                result.Headers[h.Key] = string.Join(", ", h.Value);
            if (resp.Content != null)
                foreach (var h in resp.Content.Headers)
                    result.Headers[h.Key] = string.Join(", ", h.Value);
        }

        // 非流式：读取完整响应
        public async Task<HttpResult> SendAsync(HttpRequestSpec spec, CancellationToken ct)
        {
            var result = new HttpResult();
            var sw = Stopwatch.StartNew();
            try
            {
                using var req = BuildRequest(spec);
                using HttpResponseMessage resp =
                    await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                result.Status = (int)resp.StatusCode;
                result.StatusText = resp.ReasonPhrase ?? "";
                CopyHeaders(resp, result);
                result.Body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result.Error = new OperationCanceledException("Cancelled");
            }
            catch (Exception ex)
            {
                result.Error = ex;
            }
            sw.Stop();
            result.ElapsedMs = sw.ElapsedMilliseconds;
            result.TtftMs = result.ElapsedMs;
            return result;
        }

        // 流式：逐 SSE 事件块解析；实时回调增量文本；返回末尾汇总（含 usage / 状态 / 原始 SSE）
        public async Task<HttpResult> StreamAsync(HttpRequestSpec spec, IApiProtocol proto,
            Action<long> onFirstByte, Action<string> onDelta, CancellationToken ct)
        {
            var result = new HttpResult();
            var sw = Stopwatch.StartNew();
            var raw = new StringBuilder();
            bool gotFirst = false;

            // 处理一个完整的 SSE 事件块
            void HandleBlock(string blk)
            {
                raw.Append(blk);
                SseEvent ev = proto.ParseSseEvent(blk);
                if (!string.IsNullOrEmpty(ev.TextDelta))
                {
                    if (!gotFirst)
                    {
                        gotFirst = true;
                        result.TtftMs = sw.ElapsedMilliseconds;
                        onFirstByte(result.TtftMs);
                    }
                    onDelta(ev.TextDelta!);
                }
                if (ev.PromptTokens.HasValue) result.PromptTokens = ev.PromptTokens;
                if (ev.CompletionTokens.HasValue) result.CompletionTokens = ev.CompletionTokens;
                if (ev.TotalTokens.HasValue) result.TotalTokens = ev.TotalTokens;
            }

            try
            {
                using var req = BuildRequest(spec);
                using HttpResponseMessage resp =
                    await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                result.Status = (int)resp.StatusCode;
                result.StatusText = resp.ReasonPhrase ?? "";
                CopyHeaders(resp, result);

                if (!resp.IsSuccessStatusCode)
                {
                    // 错误：读完整错误体返回
                    result.Body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
                else
                {
                    using Stream s = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    using var reader = new StreamReader(s, Encoding.UTF8);

                    var block = new StringBuilder();
                    string? line;
                    while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        if (line.Length == 0)
                        {
                            if (block.Length > 0) { HandleBlock(block.ToString()); block.Clear(); }
                            continue;
                        }
                        block.Append(line).Append('\n');
                    }
                    if (block.Length > 0) HandleBlock(block.ToString());

                    result.Body = raw.ToString();   // 原始 SSE 累积，供 "Raw" 查看
                }
            }
            catch (OperationCanceledException)
            {
                result.Error = new OperationCanceledException("Cancelled");
                if (result.Body.Length == 0) result.Body = raw.ToString();
            }
            catch (Exception ex)
            {
                result.Error = ex;
                if (result.Body.Length == 0) result.Body = raw.ToString();
            }
            sw.Stop();
            result.ElapsedMs = sw.ElapsedMilliseconds;
            return result;
        }
    }

    // SSE 工具：从一个事件块（以空行分隔，可能含 event:/data: 多行）中提取 data 字段
    internal static class SseUtil
    {
        public static string? ExtractData(string block)
        {
            var sb = new StringBuilder();
            bool has = false;
            foreach (string rawLine in block.Split('\n'))
            {
                string l = rawLine.TrimEnd('\r');
                if (l.StartsWith("data:"))
                {
                    string v = l.Substring(5);
                    if (v.StartsWith(" ")) v = v.Substring(1);   // 去掉一个前导空格
                    if (has) sb.Append('\n');                    // 多 data 行用 \n 合并
                    sb.Append(v);
                    has = true;
                }
            }
            return has ? sb.ToString() : null;
        }
    }
}
