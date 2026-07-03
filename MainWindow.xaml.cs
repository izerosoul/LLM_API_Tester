using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ApiTester
{
    public partial class MainWindow : Window
    {
        private readonly ApiClient _client = new();
        private IApiProtocol[] _protocols = Array.Empty<IApiProtocol>();
        private List<Preset> _presets = new();
        private CancellationTokenSource? _cts;
        private string _lastResponse = "";   // 最近一次响应的原始体（供 Raw / Format JSON 切换）
        private bool _suspendPreview;         // 批量改控件时抑制预览刷新

        public MainWindow()
        {
            InitializeComponent();

            _protocols = ProtocolFactory.All();
            ProtocolBox.ItemsSource = _protocols;
            ProtocolBox.DisplayMemberPath = "DisplayName";

            HookEvents();
            LoadPresetsToBox();

            ProtocolBox.SelectedIndex = 0;   // 触发协议默认值填充 + 首次预览
        }

        // ===== 事件挂接（集中在代码里，XAML 只负责布局）=====
        private void HookEvents()
        {
            ProtocolBox.SelectionChanged += (s, e) => { ApplyProtocolDefaults(); UpdatePreview(); };

            BaseUrlBox.TextChanged += (s, e) => UpdatePreview();
            KeyBox.TextChanged += (s, e) => UpdatePreview();
            AutoFillUrlBox.Checked += (s, e) =>
            {
                // 勾选时立即用当前协议默认 URL 填充（之后随协议切换自动填）
                var proto = CurrentProtocol();
                if (proto != null) BaseUrlBox.Text = proto.DefaultBaseUrl;
            };
            ModelBox.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((s, e) => UpdatePreview()));
            MaxTokensBox.TextChanged += (s, e) => UpdatePreview();
            TempBox.TextChanged += (s, e) => UpdatePreview();
            SystemBox.TextChanged += (s, e) => UpdatePreview();
            MessageBox.TextChanged += (s, e) => UpdatePreview();
            StreamBox.Checked += (s, e) => UpdatePreview();
            StreamBox.Unchecked += (s, e) => UpdatePreview();
            ShowKeyBox.Checked += (s, e) => UpdatePreview();
            ShowKeyBox.Unchecked += (s, e) => UpdatePreview();

            ListModelsBtn.Click += async (s, e) => await OnListModels();
            SendBtn.Click += async (s, e) => await OnSend();
            StopBtn.Click += (s, e) => _cts?.Cancel();
            CopyReqBtn.Click += (s, e) => CopyToClipboard(RequestBox.Text);
            FormatJsonBtn.Click += (s, e) => ResponseBox.Text = JsonUtil.Pretty(_lastResponse);
            RawRespBtn.Click += (s, e) => ResponseBox.Text = _lastResponse;
            CopyRespBtn.Click += (s, e) => CopyToClipboard(ResponseBox.Text);
            SavePresetBtn.Click += (s, e) => OnSavePreset();
            DelPresetBtn.Click += (s, e) => OnDelPreset();
            PresetBox.SelectionChanged += (s, e) => OnPresetSelected();
        }

        // 选定协议后填默认 Base URL，并按协议能力启用/禁用 temperature
        private void ApplyProtocolDefaults()
        {
            var proto = CurrentProtocol();
            if (proto == null) return;
            // 仅在勾选 Auto-fill URL 时随协议切换自动填默认 Base URL；默认不勾、不改（保持为空或用户输入）
            if (AutoFillUrlBox.IsChecked == true)
                BaseUrlBox.Text = proto.DefaultBaseUrl;
            TempBox.IsEnabled = proto.SupportsTemperature;
            if (!proto.SupportsTemperature) TempBox.Text = "";
            ModelBox.Items.Clear();
            ModelBox.Text = "";
        }

        // ===== 读取当前界面状态 =====
        private IApiProtocol? CurrentProtocol() => ProtocolBox.SelectedItem as IApiProtocol;

        private ApiConfig CurrentConfig()
        {
            var proto = CurrentProtocol();
            return new ApiConfig
            {
                Kind = proto?.Kind ?? ProtocolKind.OpenAiChat,
                BaseUrl = BaseUrlBox.Text.Trim(),
                ApiKey = KeyBox.Text,
                Model = ModelBox.Text.Trim()
            };
        }

        private ChatParams CurrentParams()
        {
            var proto = CurrentProtocol();
            int maxTok = int.TryParse(MaxTokensBox.Text.Trim(), out int mt) ? mt : 256;
            double? temp = null;
            if (proto != null && proto.SupportsTemperature
                && double.TryParse(TempBox.Text.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double tv))
                temp = tv;
            return new ChatParams
            {
                Message = MessageBox.Text,
                System = string.IsNullOrWhiteSpace(SystemBox.Text) ? null : SystemBox.Text,
                MaxTokens = maxTok,
                Temperature = temp
            };
        }

        // ===== 请求预览 =====
        private void UpdatePreview()
        {
            if (_suspendPreview) return;
            var proto = CurrentProtocol();
            if (proto == null) return;
            try
            {
                var spec = proto.BuildChat(CurrentConfig(), CurrentParams(), StreamBox.IsChecked == true);
                RequestBox.Text = RenderRequest(spec, ShowKeyBox.IsChecked == true);
            }
            catch (Exception ex)
            {
                RequestBox.Text = "(preview error) " + ex.Message;
            }
        }

        // 把请求拼成可读文本（密钥默认脱敏）
        private static string RenderRequest(HttpRequestSpec spec, bool showKey)
        {
            var sb = new StringBuilder();
            string url = showKey ? spec.Url : MaskUrlKey(spec.Url);
            sb.Append(spec.Method).Append(' ').Append(url).Append('\n');
            foreach (var kv in spec.Headers)
            {
                string val = (!showKey && IsSecretHeader(kv.Key)) ? Mask(kv.Value) : kv.Value;
                sb.Append(kv.Key).Append(": ").Append(val).Append('\n');
            }
            if (spec.Body != null) sb.Append("Content-Type: application/json\n");
            sb.Append('\n');
            if (spec.Body != null) sb.Append(JsonUtil.Pretty(spec.Body));
            return sb.ToString();
        }

        private static bool IsSecretHeader(string name)
        {
            return name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                || name.Equals("x-api-key", StringComparison.OrdinalIgnoreCase)
                || name.Equals("x-goog-api-key", StringComparison.OrdinalIgnoreCase);
        }

        // 脱敏：保留 "Bearer " 前缀，token 显示前 6 + 后 4
        private static string Mask(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            string prefix = "";
            string token = value;
            const string bearer = "Bearer ";
            if (value.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
            {
                prefix = bearer;
                token = value.Substring(bearer.Length);
            }
            string masked = token.Length <= 10
                ? new string('*', token.Length)
                : token.Substring(0, 6) + "…" + token.Substring(token.Length - 4);
            return prefix + masked;
        }

        // 脱敏 URL 中的 key= 查询参数
        private static string MaskUrlKey(string url)
        {
            return Regex.Replace(url, "(key=)([^&]+)", m => m.Groups[1].Value + Mask(m.Groups[2].Value));
        }

        // ===== 列模型 =====
        private async Task OnListModels()
        {
            var proto = CurrentProtocol();
            if (proto == null) return;
            SetBusy(true, "Listing models...");
            _cts = new CancellationTokenSource();
            HttpResult res = new();
            try
            {
                HttpRequestSpec spec = proto.BuildListModels(CurrentConfig());
                res = await _client.SendAsync(spec, _cts.Token);

                if (res.Error != null)
                {
                    StatusText.Text = res.Cancelled ? "Status: Cancelled" : "Status: ERROR";
                    if (!res.Cancelled) ResponseBox.Text = "Request error:\n" + res.Error.Message;
                }
                else if (!res.IsSuccess)
                {
                    StatusText.Text = $"Status: {res.Status} {res.StatusText}";
                    _lastResponse = res.Body;
                    ResponseBox.Text = JsonUtil.Pretty(res.Body);
                }
                else
                {
                    StatusText.Text = $"Status: {res.Status} {res.StatusText}";
                    List<string> models;
                    try { models = proto.ParseModelList(res.Body); }
                    catch { models = new List<string>(); }

                    _suspendPreview = true;
                    ModelBox.Items.Clear();
                    foreach (var m in models) ModelBox.Items.Add(m);
                    if (models.Count > 0) ModelBox.SelectedIndex = 0;
                    _suspendPreview = false;
                    UpdatePreview();

                    _lastResponse = res.Body;
                    ResponseBox.Text = $"// {models.Count} model(s)\n" + string.Join("\n", models);
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "Status: ERROR";
                ResponseBox.Text = "Error:\n" + ex.Message;
            }
            finally
            {
                TimeText.Text = $"Time: {res.ElapsedMs} ms";
                SetBusy(false, null);
            }
        }

        // ===== 发送对话 =====
        private async Task OnSend()
        {
            var proto = CurrentProtocol();
            if (proto == null) return;
            bool stream = StreamBox.IsChecked == true;

            HttpRequestSpec spec = proto.BuildChat(CurrentConfig(), CurrentParams(), stream);
            RequestBox.Text = RenderRequest(spec, ShowKeyBox.IsChecked == true);
            ResponseBox.Clear();
            _lastResponse = "";
            TtftText.Text = "TTFT: -";
            TokensText.Text = "Tokens: -";

            SetBusy(true, "Sending...");
            _cts = new CancellationTokenSource();
            HttpResult res = new();
            try
            {
                if (stream)
                {
                    res = await _client.StreamAsync(spec, proto,
                        ttft => Dispatcher.InvokeAsync(() => TtftText.Text = $"TTFT: {ttft} ms"),
                        text => Dispatcher.InvokeAsync(() => { ResponseBox.AppendText(text); ResponseBox.ScrollToEnd(); }),
                        _cts.Token);
                }
                else
                {
                    res = await _client.SendAsync(spec, _cts.Token);
                }
                _lastResponse = res.Body;
            }
            catch (Exception ex)
            {
                res = new HttpResult { Error = ex };
            }
            finally
            {
                Finish(res, proto, stream);
                SetBusy(false, null);
            }
        }

        // 发送完成后统一更新状态栏与内容展示
        private void Finish(HttpResult res, IApiProtocol proto, bool stream)
        {
            if (res.Cancelled) StatusText.Text = "Status: Cancelled";
            else if (res.Error != null) StatusText.Text = "Status: ERROR";
            else StatusText.Text = $"Status: {res.Status} {res.StatusText}";

            TimeText.Text = $"Time: {res.ElapsedMs} ms";
            TtftText.Text = $"TTFT: {res.TtftMs} ms";

            // 内容展示
            if (res.Error != null)
            {
                if (!res.Cancelled) ResponseBox.Text = "Request error:\n" + res.Error.Message;
            }
            else if (stream)
            {
                if (!res.IsSuccess) ResponseBox.Text = JsonUtil.Pretty(res.Body);   // 错误体
                // 成功：保留实时增量文本
            }
            else
            {
                ResponseBox.Text = JsonUtil.Pretty(res.Body);   // 成功响应或错误体
            }

            // Token 统计
            int? p = null, c = null, t = null;
            if (res.Error == null && res.IsSuccess)
            {
                if (stream)
                {
                    p = res.PromptTokens; c = res.CompletionTokens; t = res.TotalTokens;
                }
                else
                {
                    try { var cr = proto.ParseChatResponse(res.Body); p = cr.PromptTokens; c = cr.CompletionTokens; t = cr.TotalTokens; }
                    catch { }
                }
            }
            if (t == null && (p.HasValue || c.HasValue)) t = (p ?? 0) + (c ?? 0);
            TokensText.Text = "Tokens: " +
                ((p.HasValue || c.HasValue || t.HasValue) ? $"{Show(p)}+{Show(c)}={Show(t)}" : "-");
        }

        private static string Show(int? v) => v.HasValue ? v.Value.ToString() : "?";

        // ===== 预设 =====
        private void LoadPresetsToBox()
        {
            _presets = PresetStore.Load();
            _suspendPreview = true;
            PresetBox.Items.Clear();
            foreach (var pr in _presets) PresetBox.Items.Add(pr.Name);
            _suspendPreview = false;
        }

        private void OnPresetSelected()
        {
            if (PresetBox.SelectedItem is not string name) return;
            var pr = _presets.Find(x => x.Name == name);
            if (pr == null) return;

            _suspendPreview = true;
            var proto = Array.Find(_protocols, x => x.Kind == pr.Kind);
            if (proto != null) ProtocolBox.SelectedItem = proto;   // 触发的预览被 _suspendPreview 抑制
            BaseUrlBox.Text = pr.BaseUrl;
            if (pr.ApiKey != null) { KeyBox.Text = pr.ApiKey; RememberKeyBox.IsChecked = true; }
            _suspendPreview = false;
            UpdatePreview();
        }

        private void OnSavePreset()
        {
            string name = PresetBox.Text.Trim();
            if (string.IsNullOrEmpty(name)) { StatusText.Text = "Status: preset name is empty"; return; }
            var proto = CurrentProtocol();
            var pr = new Preset
            {
                Name = name,
                Kind = proto?.Kind ?? ProtocolKind.OpenAiChat,
                BaseUrl = BaseUrlBox.Text.Trim(),
                ApiKey = RememberKeyBox.IsChecked == true ? KeyBox.Text : null
            };
            int idx = _presets.FindIndex(x => x.Name == name);
            if (idx >= 0) _presets[idx] = pr; else _presets.Add(pr);
            PresetStore.Save(_presets);
            RefreshPresetBox(name);
            StatusText.Text = $"Status: preset '{name}' saved";
        }

        private void OnDelPreset()
        {
            string name = PresetBox.Text.Trim();
            int idx = _presets.FindIndex(x => x.Name == name);
            if (idx < 0) { StatusText.Text = "Status: preset not found"; return; }
            _presets.RemoveAt(idx);
            PresetStore.Save(_presets);
            RefreshPresetBox("");
            StatusText.Text = $"Status: preset '{name}' deleted";
        }

        private void RefreshPresetBox(string selectName)
        {
            _suspendPreview = true;
            PresetBox.Items.Clear();
            foreach (var pr in _presets) PresetBox.Items.Add(pr.Name);
            PresetBox.Text = selectName;
            _suspendPreview = false;
        }

        // ===== 杂项 =====
        private void SetBusy(bool busy, string? status)
        {
            ListModelsBtn.IsEnabled = !busy;
            SendBtn.IsEnabled = !busy;
            StopBtn.IsEnabled = busy;
            if (status != null) StatusText.Text = "Status: " + status;
        }

        private static void CopyToClipboard(string text)
        {
            try { Clipboard.SetText(text ?? ""); } catch { /* 剪贴板偶发占用，忽略 */ }
        }
    }
}
