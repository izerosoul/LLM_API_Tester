using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
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
        public string HttpVersion = "1.1";
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

    internal sealed class ProxyConfigException : Exception
    {
        public ProxyConfigException(string message) : base(message) { }
    }

    // 基于 HttpClient 的执行层：非流式 + SSE 流式
    public sealed class ApiClient
    {
        // 单实例复用（无代理时使用）
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            // .NET Framework 默认协议在旧系统上可能偏保守，这里显式启用 TLS 1.2。
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var c = new HttpClient();
            c.Timeout = Timeout.InfiniteTimeSpan;
            return c;
        }

        private static HttpClient CreateHttpProxyClient(ApiProxyConfig proxy)
        {
            ValidateProxy(proxy);
            var webProxy = new WebProxy(proxy.Host, proxy.Port);
            if (!string.IsNullOrWhiteSpace(proxy.User))
                webProxy.Credentials = new NetworkCredential(proxy.User, proxy.Password ?? "");

            var handler = new HttpClientHandler
            {
                UseProxy = true,
                Proxy = webProxy,
                PreAuthenticate = true
            };
            var client = new HttpClient(handler);
            client.Timeout = Timeout.InfiniteTimeSpan;
            return client;
        }

        private static void ValidateProxy(ApiProxyConfig? proxy)
        {
            if (proxy == null || proxy.Kind == ProxyKind.None) return;
            if (string.IsNullOrWhiteSpace(proxy.Host))
                throw new ProxyConfigException("Proxy host is empty.");
            if (proxy.Port <= 0 || proxy.Port > 65535)
                throw new ProxyConfigException("Proxy port must be between 1 and 65535.");
        }

        private static HttpRequestMessage BuildRequest(HttpRequestSpec spec)
        {
            var req = new HttpRequestMessage(new HttpMethod(spec.Method), spec.Url);
            if (spec.Body != null)
                req.Content = new StringContent(spec.Body, Encoding.UTF8, "application/json");
            foreach (var kv in spec.Headers)
            {
                if (string.Equals(kv.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.Equals(kv.Key, "Host", StringComparison.OrdinalIgnoreCase))
                {
                    req.Headers.Host = kv.Value;
                    continue;
                }
                if (req.Content != null && kv.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                {
                    req.Content.Headers.Remove(kv.Key);
                    req.Content.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                    continue;
                }
                req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }
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

        private static Exception BuildCancellationError(CancellationToken userToken,
            CancellationToken timeoutToken, TimeSpan timeout, Exception inner)
        {
            if (timeoutToken.IsCancellationRequested && !userToken.IsCancellationRequested)
                return new TimeoutException($"Timed out after {FormatTimeout(timeout)}.", inner);
            return new OperationCanceledException("Cancelled");
        }

        private static string FormatTimeout(TimeSpan timeout)
        {
            double seconds = timeout.TotalSeconds;
            return Math.Abs(seconds - Math.Round(seconds)) < 0.001
                ? ((int)Math.Round(seconds)).ToString() + " seconds"
                : seconds.ToString("0.###") + " seconds";
        }

        // 非流式：读取完整响应
        public async Task<HttpResult> SendAsync(HttpRequestSpec spec, TimeSpan timeout, CancellationToken ct, ApiProxyConfig? proxy = null)
        {
            if (proxy != null && proxy.Kind == ProxyKind.Socks5)
                return await SendViaSocks5Async(spec, proxy, timeout, ct).ConfigureAwait(false);

            var result = new HttpResult();
            var sw = Stopwatch.StartNew();
            using var timeoutCts = new CancellationTokenSource();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            if (timeout > TimeSpan.Zero) timeoutCts.CancelAfter(timeout);
            HttpClient client = Http;
            HttpClient? disposableClient = null;
            try
            {
                if (proxy != null && proxy.Kind == ProxyKind.Http)
                    client = disposableClient = CreateHttpProxyClient(proxy);
                using var req = BuildRequest(spec);
                using HttpResponseMessage resp =
                    await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, linkedCts.Token).ConfigureAwait(false);
                result.Status = (int)resp.StatusCode;
                result.StatusText = resp.ReasonPhrase ?? "";
                result.HttpVersion = resp.Version?.ToString() ?? "1.1";
                CopyHeaders(resp, result);
                result.Body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                result.Error = BuildCancellationError(ct, timeoutCts.Token, timeout, ex);
            }
            catch (Exception ex)
            {
                result.Error = ex;
            }
            finally
            {
                disposableClient?.Dispose();
            }
            sw.Stop();
            result.ElapsedMs = sw.ElapsedMilliseconds;
            result.TtftMs = result.ElapsedMs;
            return result;
        }

        // 流式：逐 SSE 事件块解析；实时回调增量文本；返回末尾汇总（含 usage / 状态 / 原始 SSE）
        public async Task<HttpResult> StreamAsync(HttpRequestSpec spec, IApiProtocol proto,
            Action<long> onFirstByte, Action<string> onDelta, TimeSpan timeout, CancellationToken ct,
            ApiProxyConfig? proxy = null)
        {
            if (proxy != null && proxy.Kind == ProxyKind.Socks5)
                return await StreamViaSocks5Async(spec, proto, onFirstByte, onDelta, timeout, ct, proxy).ConfigureAwait(false);

            var result = new HttpResult();
            var sw = Stopwatch.StartNew();
            var raw = new StringBuilder();
            bool gotFirst = false;
            using var timeoutCts = new CancellationTokenSource();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            if (timeout > TimeSpan.Zero) timeoutCts.CancelAfter(timeout);
            CancellationToken requestToken = linkedCts.Token;
            HttpClient client = Http;
            HttpClient? disposableClient = null;

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
                if (proxy != null && proxy.Kind == ProxyKind.Http)
                    client = disposableClient = CreateHttpProxyClient(proxy);
                using var req = BuildRequest(spec);
                using HttpResponseMessage resp =
                    await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, requestToken).ConfigureAwait(false);
                result.Status = (int)resp.StatusCode;
                result.StatusText = resp.ReasonPhrase ?? "";
                result.HttpVersion = resp.Version?.ToString() ?? "1.1";
                CopyHeaders(resp, result);

                if (!resp.IsSuccessStatusCode)
                {
                    // 错误：读完整错误体返回
                    using Stream s = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    using var cancelRead = requestToken.Register(() => { try { s.Dispose(); } catch { } });
                    using var reader = new StreamReader(s, Encoding.UTF8);
                    result.Body = await reader.ReadToEndAsync().ConfigureAwait(false);
                    requestToken.ThrowIfCancellationRequested();
                }
                else
                {
                    using Stream s = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    using var cancelRead = requestToken.Register(() => { try { s.Dispose(); } catch { } });
                    using var reader = new StreamReader(s, Encoding.UTF8);

                    var block = new StringBuilder();
                    string? line;
                    while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        requestToken.ThrowIfCancellationRequested();
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
            catch (OperationCanceledException ex)
            {
                result.Error = BuildCancellationError(ct, timeoutCts.Token, timeout, ex);
                if (result.Body.Length == 0) result.Body = raw.ToString();
            }
            catch (Exception ex) when (requestToken.IsCancellationRequested && (ex is IOException || ex is ObjectDisposedException))
            {
                result.Error = BuildCancellationError(ct, timeoutCts.Token, timeout, ex);
                if (result.Body.Length == 0) result.Body = raw.ToString();
            }
            catch (Exception ex)
            {
                result.Error = ex;
                if (result.Body.Length == 0) result.Body = raw.ToString();
            }
            finally
            {
                disposableClient?.Dispose();
            }
            sw.Stop();
            result.ElapsedMs = sw.ElapsedMilliseconds;
            return result;
        }

        private static async Task<HttpResult> SendViaSocks5Async(HttpRequestSpec spec, ApiProxyConfig proxy,
            TimeSpan timeout, CancellationToken ct)
        {
            var result = new HttpResult();
            var sw = Stopwatch.StartNew();
            using var timeoutCts = new CancellationTokenSource();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            if (timeout > TimeSpan.Zero) timeoutCts.CancelAfter(timeout);
            CancellationToken token = linkedCts.Token;

            try
            {
                ValidateProxy(proxy);
                Uri uri = RequireAbsoluteUri(spec.Url);
                using TcpClient tcp = await ConnectTcpAsync(proxy.Host, proxy.Port, token).ConfigureAwait(false);
                Stream stream = tcp.GetStream();
                await Socks5ConnectAsync(stream, uri.Host, uri.Port, proxy, token).ConfigureAwait(false);

                SslStream? ssl = null;
                try
                {
                    if (IsHttps(uri))
                    {
                        ssl = new SslStream(stream, false);
                        await AwaitWithCancellation(ssl.AuthenticateAsClientAsync(uri.Host), token).ConfigureAwait(false);
                        stream = ssl;
                    }

                    await WriteRawRequestAsync(stream, spec, uri, token).ConfigureAwait(false);
                    await ReadRawResponseAsync(stream, result, token).ConfigureAwait(false);
                }
                finally
                {
                    ssl?.Dispose();
                }
            }
            catch (OperationCanceledException ex)
            {
                result.Error = BuildCancellationError(ct, timeoutCts.Token, timeout, ex);
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

        private static async Task<HttpResult> StreamViaSocks5Async(HttpRequestSpec spec, IApiProtocol proto,
            Action<long> onFirstByte, Action<string> onDelta, TimeSpan timeout, CancellationToken ct,
            ApiProxyConfig proxy)
        {
            var result = new HttpResult();
            var sw = Stopwatch.StartNew();
            using var timeoutCts = new CancellationTokenSource();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            if (timeout > TimeSpan.Zero) timeoutCts.CancelAfter(timeout);
            CancellationToken token = linkedCts.Token;

            try
            {
                ValidateProxy(proxy);
                Uri uri = RequireAbsoluteUri(spec.Url);
                using TcpClient tcp = await ConnectTcpAsync(proxy.Host, proxy.Port, token).ConfigureAwait(false);
                Stream stream = tcp.GetStream();
                await Socks5ConnectAsync(stream, uri.Host, uri.Port, proxy, token).ConfigureAwait(false);

                SslStream? ssl = null;
                try
                {
                    if (IsHttps(uri))
                    {
                        ssl = new SslStream(stream, false);
                        await AwaitWithCancellation(ssl.AuthenticateAsClientAsync(uri.Host), token).ConfigureAwait(false);
                        stream = ssl;
                    }

                    await WriteRawRequestAsync(stream, spec, uri, token).ConfigureAwait(false);
                    await ReadRawResponseHeadersAsync(stream, result, token).ConfigureAwait(false);

                    if (!result.IsSuccess)
                    {
                        result.Body = await ReadResponseBodyAsync(stream, result, token).ConfigureAwait(false);
                    }
                    else
                    {
                        await ReadSseBodyAsync(stream, result, proto, onFirstByte, onDelta, sw, token).ConfigureAwait(false);
                    }
                }
                finally
                {
                    ssl?.Dispose();
                }
            }
            catch (OperationCanceledException ex)
            {
                result.Error = BuildCancellationError(ct, timeoutCts.Token, timeout, ex);
            }
            catch (Exception ex)
            {
                result.Error = ex;
            }

            sw.Stop();
            result.ElapsedMs = sw.ElapsedMilliseconds;
            return result;
        }

        private static Uri RequireAbsoluteUri(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
                throw new InvalidOperationException("Request URL must be absolute.");
            if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SOCKS5 proxy supports HTTP and HTTPS URLs only.");
            return uri;
        }

        private static bool IsHttps(Uri uri)
        {
            return string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<TcpClient> ConnectTcpAsync(string host, int port, CancellationToken token)
        {
            var client = new TcpClient();
            try
            {
                Task connectTask = client.ConnectAsync(host, port);
                await AwaitWithCancellation(connectTask, token).ConfigureAwait(false);
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        private static async Task AwaitWithCancellation(Task task, CancellationToken token)
        {
            Task cancelTask = Task.Delay(Timeout.InfiniteTimeSpan, token);
            Task completed = await Task.WhenAny(task, cancelTask).ConfigureAwait(false);
            if (completed != task)
                throw new OperationCanceledException(token);
            await task.ConfigureAwait(false);
        }

        private static async Task Socks5ConnectAsync(Stream stream, string targetHost, int targetPort,
            ApiProxyConfig proxy, CancellationToken token)
        {
            bool hasAuth = !string.IsNullOrWhiteSpace(proxy.User);
            byte[] greeting = hasAuth
                ? new byte[] { 0x05, 0x02, 0x00, 0x02 }
                : new byte[] { 0x05, 0x01, 0x00 };
            await stream.WriteAsync(greeting, 0, greeting.Length, token).ConfigureAwait(false);

            byte[] response = await ReadExactBytesAsync(stream, 2, token).ConfigureAwait(false);
            if (response[0] != 0x05)
                throw new IOException("Invalid SOCKS5 greeting response.");
            if (response[1] == 0xFF)
                throw new IOException("SOCKS5 proxy rejected authentication methods.");
            if (response[1] == 0x02)
                await Socks5AuthenticateAsync(stream, proxy, token).ConfigureAwait(false);
            else if (response[1] != 0x00)
                throw new IOException("Unsupported SOCKS5 authentication method: " + response[1]);

            byte[] hostBytes = Encoding.ASCII.GetBytes(targetHost);
            if (hostBytes.Length > 255)
                throw new IOException("SOCKS5 target host is too long.");

            var request = new MemoryStream();
            request.WriteByte(0x05);
            request.WriteByte(0x01);
            request.WriteByte(0x00);
            request.WriteByte(0x03);
            request.WriteByte((byte)hostBytes.Length);
            request.Write(hostBytes, 0, hostBytes.Length);
            request.WriteByte((byte)((targetPort >> 8) & 0xFF));
            request.WriteByte((byte)(targetPort & 0xFF));
            byte[] data = request.ToArray();
            await stream.WriteAsync(data, 0, data.Length, token).ConfigureAwait(false);

            byte[] head = await ReadExactBytesAsync(stream, 4, token).ConfigureAwait(false);
            if (head[0] != 0x05)
                throw new IOException("Invalid SOCKS5 connect response.");
            if (head[1] != 0x00)
                throw new IOException("SOCKS5 connect failed: " + Socks5ReplyText(head[1]));

            int addressLength;
            switch (head[3])
            {
                case 0x01: addressLength = 4; break;
                case 0x03:
                    byte[] len = await ReadExactBytesAsync(stream, 1, token).ConfigureAwait(false);
                    addressLength = len[0];
                    break;
                case 0x04: addressLength = 16; break;
                default: throw new IOException("Invalid SOCKS5 address type.");
            }
            await ReadExactBytesAsync(stream, addressLength + 2, token).ConfigureAwait(false);
        }

        private static async Task Socks5AuthenticateAsync(Stream stream, ApiProxyConfig proxy, CancellationToken token)
        {
            byte[] user = Encoding.UTF8.GetBytes(proxy.User ?? "");
            byte[] pass = Encoding.UTF8.GetBytes(proxy.Password ?? "");
            if (user.Length > 255 || pass.Length > 255)
                throw new IOException("SOCKS5 username/password is too long.");

            var auth = new MemoryStream();
            auth.WriteByte(0x01);
            auth.WriteByte((byte)user.Length);
            auth.Write(user, 0, user.Length);
            auth.WriteByte((byte)pass.Length);
            auth.Write(pass, 0, pass.Length);
            byte[] data = auth.ToArray();
            await stream.WriteAsync(data, 0, data.Length, token).ConfigureAwait(false);

            byte[] response = await ReadExactBytesAsync(stream, 2, token).ConfigureAwait(false);
            if (response[1] != 0x00)
                throw new IOException("SOCKS5 username/password authentication failed.");
        }

        private static string Socks5ReplyText(byte code)
        {
            switch (code)
            {
                case 0x01: return "general failure";
                case 0x02: return "connection not allowed";
                case 0x03: return "network unreachable";
                case 0x04: return "host unreachable";
                case 0x05: return "connection refused";
                case 0x06: return "TTL expired";
                case 0x07: return "command not supported";
                case 0x08: return "address type not supported";
                default: return "error " + code;
            }
        }

        private static async Task WriteRawRequestAsync(Stream stream, HttpRequestSpec spec, Uri uri,
            CancellationToken token)
        {
            byte[]? bodyBytes = spec.Body == null ? null : Encoding.UTF8.GetBytes(spec.Body);
            var sb = new StringBuilder();
            string target = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
            sb.Append(spec.Method).Append(' ').Append(target).Append(" HTTP/1.1\r\n");
            if (!HasHeader(spec.Headers, "Host"))
                sb.Append("Host: ").Append(uri.Authority).Append("\r\n");

            foreach (var kv in spec.Headers)
            {
                if (string.Equals(kv.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                    continue;
                sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
            }
            if (bodyBytes != null && !HasHeader(spec.Headers, "Content-Type"))
                sb.Append("Content-Type: application/json\r\n");
            if (bodyBytes != null)
                sb.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
            if (!HasHeader(spec.Headers, "Connection"))
                sb.Append("Connection: close\r\n");
            sb.Append("\r\n");

            byte[] headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
            await stream.WriteAsync(headerBytes, 0, headerBytes.Length, token).ConfigureAwait(false);
            if (bodyBytes != null)
                await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }

        private static bool HasHeader(Dictionary<string, string> headers, string name)
        {
            foreach (var key in headers.Keys)
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static async Task ReadRawResponseAsync(Stream stream, HttpResult result, CancellationToken token)
        {
            await ReadRawResponseHeadersAsync(stream, result, token).ConfigureAwait(false);
            result.Body = await ReadResponseBodyAsync(stream, result, token).ConfigureAwait(false);
        }

        private static async Task ReadRawResponseHeadersAsync(Stream stream, HttpResult result, CancellationToken token)
        {
            string headerText = await ReadHeaderTextAsync(stream, token).ConfigureAwait(false);
            string[] lines = headerText.Replace("\r\n", "\n").Split('\n');
            if (lines.Length == 0 || !lines[0].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Invalid HTTP response.");

            string[] statusParts = lines[0].Trim().Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
            if (statusParts.Length >= 1)
                result.HttpVersion = statusParts[0].Substring("HTTP/".Length);
            if (statusParts.Length >= 2 && int.TryParse(statusParts[1], out int status))
                result.Status = status;
            if (statusParts.Length >= 3)
                result.StatusText = statusParts[2];

            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string key = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                if (result.Headers.ContainsKey(key))
                    result.Headers[key] += ", " + value;
                else
                    result.Headers[key] = value;
            }
        }

        private static async Task<string> ReadHeaderTextAsync(Stream stream, CancellationToken token)
        {
            var bytes = new List<byte>();
            while (true)
            {
                int b = await ReadByteAsync(stream, token).ConfigureAwait(false);
                if (b < 0) throw new EndOfStreamException("Unexpected end of response headers.");
                bytes.Add((byte)b);
                int n = bytes.Count;
                if (n >= 4 && bytes[n - 4] == '\r' && bytes[n - 3] == '\n' && bytes[n - 2] == '\r' && bytes[n - 1] == '\n')
                    break;
                if (bytes.Count > 1024 * 1024)
                    throw new IOException("HTTP response headers are too large.");
            }
            return Encoding.ASCII.GetString(bytes.ToArray());
        }

        private static async Task<string> ReadResponseBodyAsync(Stream stream, HttpResult result, CancellationToken token)
        {
            byte[] bytes;
            if (IsChunked(result))
                bytes = await ReadChunkedBytesAsync(stream, token).ConfigureAwait(false);
            else if (TryContentLength(result, out long length))
                bytes = await ReadExactBytesAsync(stream, checked((int)length), token).ConfigureAwait(false);
            else
                bytes = await ReadToEndBytesAsync(stream, token).ConfigureAwait(false);
            return ResponseEncoding(result).GetString(bytes);
        }

        private static async Task<byte[]> ReadChunkedBytesAsync(Stream stream, CancellationToken token)
        {
            var output = new MemoryStream();
            while (true)
            {
                string? line = await ReadAsciiLineAsync(stream, token).ConfigureAwait(false);
                if (line == null) break;
                int semi = line.IndexOf(';');
                string sizeText = semi >= 0 ? line.Substring(0, semi) : line;
                int size = Convert.ToInt32(sizeText.Trim(), 16);
                if (size == 0)
                {
                    while (!string.IsNullOrEmpty(await ReadAsciiLineAsync(stream, token).ConfigureAwait(false))) { }
                    break;
                }
                byte[] chunk = await ReadExactBytesAsync(stream, size, token).ConfigureAwait(false);
                output.Write(chunk, 0, chunk.Length);
                await ReadExactBytesAsync(stream, 2, token).ConfigureAwait(false); // CRLF
            }
            return output.ToArray();
        }

        private static async Task ReadSseBodyAsync(Stream stream, HttpResult result, IApiProtocol proto,
            Action<long> onFirstByte, Action<string> onDelta, Stopwatch sw, CancellationToken token)
        {
            var raw = new StringBuilder();
            var block = new StringBuilder();
            var line = new StringBuilder();
            bool gotFirst = false;
            Decoder decoder = ResponseEncoding(result).GetDecoder();

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

            void ProcessChars(char[] chars, int count)
            {
                for (int i = 0; i < count; i++)
                {
                    char ch = chars[i];
                    if (ch == '\n')
                    {
                        string text = line.ToString().TrimEnd('\r');
                        line.Clear();
                        if (text.Length == 0)
                        {
                            if (block.Length > 0)
                            {
                                HandleBlock(block.ToString());
                                block.Clear();
                            }
                        }
                        else
                        {
                            block.Append(text).Append('\n');
                        }
                    }
                    else
                    {
                        line.Append(ch);
                    }
                }
            }

            async Task ProcessBytesAsync(byte[] buffer, int count)
            {
                char[] chars = new char[ResponseEncoding(result).GetMaxCharCount(count)];
                int charCount = decoder.GetChars(buffer, 0, count, chars, 0, false);
                ProcessChars(chars, charCount);
                await Task.CompletedTask.ConfigureAwait(false);
            }

            if (IsChunked(result))
            {
                while (true)
                {
                    string? sizeLine = await ReadAsciiLineAsync(stream, token).ConfigureAwait(false);
                    if (sizeLine == null) break;
                    int semi = sizeLine.IndexOf(';');
                    string sizeText = semi >= 0 ? sizeLine.Substring(0, semi) : sizeLine;
                    int size = Convert.ToInt32(sizeText.Trim(), 16);
                    if (size == 0)
                    {
                        while (!string.IsNullOrEmpty(await ReadAsciiLineAsync(stream, token).ConfigureAwait(false))) { }
                        break;
                    }
                    byte[] chunk = await ReadExactBytesAsync(stream, size, token).ConfigureAwait(false);
                    await ProcessBytesAsync(chunk, chunk.Length).ConfigureAwait(false);
                    await ReadExactBytesAsync(stream, 2, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                }
            }
            else
            {
                var buffer = new byte[8192];
                int read;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                {
                    await ProcessBytesAsync(buffer, read).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                }
            }

            char[] flush = new char[ResponseEncoding(result).GetMaxCharCount(0) + 8];
            int flushed = decoder.GetChars(Array.Empty<byte>(), 0, 0, flush, 0, true);
            if (flushed > 0) ProcessChars(flush, flushed);
            if (line.Length > 0) block.Append(line).Append('\n');
            if (block.Length > 0) HandleBlock(block.ToString());
            result.Body = raw.ToString();
        }

        private static bool IsChunked(HttpResult result)
        {
            string? value = HeaderValue(result, "Transfer-Encoding");
            return value != null && value.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool TryContentLength(HttpResult result, out long length)
        {
            length = 0;
            string? value = HeaderValue(result, "Content-Length");
            return value != null && long.TryParse(value.Trim(), out length) && length >= 0 && length <= int.MaxValue;
        }

        private static Encoding ResponseEncoding(HttpResult result)
        {
            string? contentType = HeaderValue(result, "Content-Type");
            if (contentType != null)
            {
                foreach (string part in contentType.Split(';'))
                {
                    string trimmed = part.Trim();
                    if (trimmed.StartsWith("charset=", StringComparison.OrdinalIgnoreCase))
                    {
                        string name = trimmed.Substring("charset=".Length).Trim().Trim('"');
                        try { return Encoding.GetEncoding(name); } catch { }
                    }
                }
            }
            return Encoding.UTF8;
        }

        private static string? HeaderValue(HttpResult result, string name)
        {
            foreach (var kv in result.Headers)
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            return null;
        }

        private static async Task<byte[]> ReadExactBytesAsync(Stream stream, int count, CancellationToken token)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = await stream.ReadAsync(buffer, offset, count - offset, token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException();
                offset += read;
            }
            return buffer;
        }

        private static async Task<byte[]> ReadToEndBytesAsync(Stream stream, CancellationToken token)
        {
            var output = new MemoryStream();
            byte[] buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                output.Write(buffer, 0, read);
            return output.ToArray();
        }

        private static async Task<int> ReadByteAsync(Stream stream, CancellationToken token)
        {
            byte[] buffer = new byte[1];
            int read = await stream.ReadAsync(buffer, 0, 1, token).ConfigureAwait(false);
            return read == 0 ? -1 : buffer[0];
        }

        private static async Task<string?> ReadAsciiLineAsync(Stream stream, CancellationToken token)
        {
            var bytes = new List<byte>();
            while (true)
            {
                int b = await ReadByteAsync(stream, token).ConfigureAwait(false);
                if (b < 0) return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
                if (b == '\n') return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
                bytes.Add((byte)b);
            }
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
