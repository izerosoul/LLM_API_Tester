# APITest 会话交接文档 (Session Handoff)

> 用途:把本会话的全部上下文交接给新的 agent,使其能无缝接手继续开发。**截至 2026-06-09。** 与用户交流请用**中文**。

---

## 0. 一句话

为用户做一个 **WPF GUI 工具**,方便地测试各家大模型 API 的连通性与状态(列模型 / 发 Hello / 看格式化响应 / 状态码·耗时·Token / 流式)。代码**已全部写完并能编译运行**(`dotnet run` 退出码 0、窗口可启动);**尚未用真实 API Key 做端到端验证**。

---

## 1. 产品目标 / 需求

单窗口桌面 GUI,核心能力:
- 输入 **Base URL** + **API Key**,选 **协议**。
- **List Models**:拉取模型列表 → 填模型下拉。
- **发送前预览**:把将要发往 API 的完整内容(方法 + URL + 头 + body)显示出来。
- **响应窗口** + **JSON 格式化**(美化缩进)。
- 发 **Hello** 测试。
- 支持 4 种主流协议:**OpenAI Chat / Claude(用户叫"Cloud") / Gemini / OpenAI Responses(用户叫"Codex")**。
- 用户认可的增强功能(已实现):**状态码 / 耗时(ms) / 首字耗时 TTFT / Token 用量统计**、**流式 SSE 实时输出**、**参数面板(消息 / system / temperature / max_tokens)**、**连接预设保存(可选记住 Key)**、**原始 HTTP 查看 / 复制按钮**。

---

## 2. 当前状态

| 项 | 状态 |
|---|---|
| 全部源码编写 | ✅ 完成 |
| 编译 (`dotnet build` / `dotnet run`) | ✅ 用户跑 `dotnet run` 退出码 0,WPF 窗口可启动 |
| 端到端功能(List Models / Send / 流式 / Format JSON / 预设) | ⏳ 未用真实 Key 验证 |
| `Build.bat` | ❌ **缺失**(全局 CLAUDE.md 要求"每次自动生成 Build.bat",当前用 dotnet 命令,尚未补) |

任务板(TaskList)进度:1~7 已完成(骨架/接口/适配器/ApiClient/Json+预设/界面/编译),**任务 8 冒烟验证 = pending**。

---

## 3. 环境与工具链(本会话探测到的事实)

- OS:Windows 10 Enterprise LTSC 2021。工作目录:`D:\Dev\CSharp\APITest`(非 git 仓库)。
- **.NET 10 SDK 已装**:`C:\Program Files\dotnet\sdk\10.0.300`、`10.0.102`(另有 `C:\Program Files (x86)\dotnet\dotnet.exe`)。
- **WPF 桌面运行时**:`Microsoft.WindowsDesktop.App` 8.0.23 / 10.0.2 / 10.0.8。
- 探测时:**无 VS、无 MSBuild on PATH、无 .NET Framework 4.8 引用程序集(targeting pack)**(`Reference Assemblies\...\.NETFramework\v4.8` 不存在)。
- **2026-06-09 全局 CLAUDE.md 更新后**:用户声明已装 **git bash, scoop, gcc, npm, bun, VS2026**。→ **现在应当有 MSBuild 了**(VS2026),这改变了 .NET Framework 4.8 路径的可行性(见 §6 分叉)。
- 平台命令分类器(见 §13)在本会话期间一度 `temporarily unavailable`(429),导致 agent 自身无法跑 Bash/PowerShell;**与代码无关,是平台瞬时问题**。用户用 `!dotnet run` 自行执行成功(`!` 前缀绕开该判定)。

---

## 4. 用户偏好与约束

- **与用户交流一律用中文**(用户明确要求)。
- 代码注释用**中文**;程序界面文字 / 输入输出用**英文**。
- 全局 `CLAUDE.md`(2026-06-09 版)要点:
  - 通用:Windows;已装 git bash/scoop/gcc/npm/bun/VS2026;默认开发 Windows 桌面程序;非必要不依赖第三方环境和库。
  - 默认行为:**先检查当前 Shell 环境,尽量用对应 Shell 的命令**。
  - C# GUI 偏好:**目标框架 .NET Framework 4.8**、**默认 WPF**、**每次自动生成 `Build.bat`**。
- 工作方式:用户喜欢**先沟通对齐再写代码**;决策点用选择题确认过(框架/协议/功能/编译方式)。

---

## 5. 关键技术决策与演变史(避免走回头路)

决策是**逐步演变**的,务必理解"为什么":

1. 最初(旧全局偏好):.NET Framework **4.0** + **WinForms** + `csc.exe`。
2. agent 指出 4.0 对 HTTPS 云端 API 的硬伤(TLS 1.2 枚举缺失、无 HttpClient、无 async)→ 用户选 **.NET 4.8**。
3. 用户中途改要 **WPF**;再选 **XAML + .csproj**(而非纯代码 WPF)。
4. 探测发现机器有 **.NET 10 SDK**、无 4.8 targeting pack、无 MSBuild → 落地为 **`net10.0-windows` + WPF + XAML + `dotnet build`**(现代 .NET,机器开箱即用,无需装东西)。
5. 因此当前实现的技术栈:
   - 目标框架 **`net10.0-windows`**,`<UseWPF>true</UseWPF>`,`OutputType=WinExe`,**零第三方 NuGet 包**。
   - HTTP:**HttpClient + async/await**(流式用 `ResponseHeadersRead` + `ReadLineAsync` 读 SSE,`CancellationToken` 取消)。
   - JSON:**内置 System.Text.Json**(解析 + 美化;美化用 `JsonNode` + `WriteIndented` + `UnsafeRelaxedJsonEscaping`;序列化请求体用 `DefaultIgnoreCondition = WhenWritingNull` 让可空字段如 temperature/system 不发送)。
   - 线程:async/await + WPF SynchronizationContext(await 后自动回 UI 线程);**流式回调跨线程,用 `Dispatcher.InvokeAsync` 包装**。
   - TLS:.NET 默认 TLS 1.2/1.3,无需手动设。
   - 预设 Key 加密:现代 .NET 的 DPAPI 需引 NuGet 包 → 为保零包**放弃加密**,改为"可选明文保存(Remember key 复选框)+ 预览脱敏"。

---

## 6. ⚠️ 重要分叉与待办(交接重点,别丢)

### (A) 目标框架冲突:net10 vs .NET Framework 4.8
- **现状**:项目实际是 `net10.0-windows`。
- **全局偏好(更新后)**:`.NET Framework 4.8`。
- **新变量**:现在装了 **VS2026** → 大概率有 MSBuild + (若装了 .NET 桌面工作负载)4.8 targeting pack,所以 **net48 + WPF 现在可能可行了**(探测时不可行)。
- **必须与用户确认**:保持 `net10.0-windows`,还是迁回 `.NET Framework 4.8`(更贴合全局偏好)?
- **若迁 net48,代码要改这些**(差异很大):
  | 方面 | net10(现状) | net48(若迁) |
  |---|---|---|
  | JSON | System.Text.Json **内置** | System.Text.Json **需 NuGet 包** → 违反零包,改用 `JavaScriptSerializer`(System.Web.Extensions,内置)解析 + **自写 JSON 美化器** + 字典构造 body |
  | Key 加密 | DPAPI 需包,故明文 | **DPAPI 内置**(System.Security `ProtectedData`),可加密 Key |
  | HttpClient | 内置 | 内置(System.Net.Http) |
  | async/await | 可用 | 可用(VS2026 Roslyn 编译) |
  | 编译 | `dotnet build` | MSBuild(VS2026) 或 `dotnet build`(SDK 风格 csproj + net48,需 4.8 targeting pack) |
  | TLS | 默认 1.2/1.3 | 建议显式 `ServicePointManager.SecurityProtocol = Tls12`(保险) |
  - 注:最初 net48 计划(JavaScriptSerializer + 自写美化器 + 后台线程)曾写在计划文件早期版本,可参考。

### (B) Build.bat 缺失
- 全局要求"每次自动生成 Build.bat",**目前没有**。
- 待补:若保持 net10 → `Build.bat` 封装 `dotnet build`(或 `dotnet build -c Release` / `dotnet run`);若迁 net48 → 用 MSBuild。

### (C) 其它待办 / 风险
- 端到端**未用真实 Key 验证**;各协议的响应/SSE 解析基于文档,真实流量下可能需微调(尤其 Responses 的 `output[]` 结构、Gemini 的 `usageMetadata` 字段名、各家 SSE 事件名)。
- Key **明文**存储(勾 Remember key 时);未加密。
- "先检查当前 Shell 环境"是新加的默认行为,后续执行命令前应遵守。

---

## 7. 文件清单与职责(`D:\Dev\CSharp\APITest\`)

| 文件 | 职责 |
|---|---|
| `ApiTester.csproj` | SDK 风格;`net10.0-windows` + `UseWPF` + `OutputType=WinExe` + `Nullable=enable` + `LangVersion=latest` + `ImplicitUsings=disable`;**无 PackageReference** |
| `App.xaml` / `App.xaml.cs` | WPF 入口,`StartupUri=MainWindow.xaml` |
| `MainWindow.xaml` | 界面布局(连接/参数区 + 左请求预览 / 右响应 + 状态栏);所有控件只有 `x:Name`,**不在 XAML 挂事件** |
| `MainWindow.xaml.cs` | 全部交互逻辑:事件挂接、协议默认值、实时请求预览(脱敏)、List Models、Send/Stop、流式、Format JSON/Raw/Copy、预设、状态栏 |
| `ApiProtocol.cs` | `IApiProtocol` 接口 + 共享类型(`HttpRequestSpec`/`ApiConfig`/`ChatParams`/`ChatResult`/`SseEvent`/`ProtocolKind`) + `ProtocolFactory` + `ProtocolUtil.TrimBase` |
| `OpenAiChatProtocol.cs` | OpenAI Chat Completions 适配器 |
| `ClaudeProtocol.cs` | Claude/Anthropic 适配器(`SupportsTemperature=false`) |
| `GeminiProtocol.cs` | Google Gemini 适配器(处理 `models/` 前缀、`alt=sse`) |
| `OpenAiResponsesProtocol.cs` | OpenAI Responses(Codex) 适配器 |
| `ApiClient.cs` | `HttpClient` 执行层:`SendAsync`(非流式) / `StreamAsync`(SSE),计时/TTFT/取消;含 `SseUtil.ExtractData`(按空行分块取 data) |
| `Json.cs` | `JsonUtil`:`Serialize`(忽略 null) / `Pretty`(失败回退原文) / `Parse`/`TryParse` / `GetString`/`GetInt`(路径式) |
| `Presets.cs` | `Preset` + `PresetStore.Load/Save`;存 `%APPDATA%\ApiTester\presets.json`,`JsonStringEnumConverter` |

---

## 8. 架构要点

### 协议适配层(差异收敛于此,UI 通用)
```csharp
enum ProtocolKind { OpenAiChat, Claude, Gemini, OpenAiResponses }

sealed class HttpRequestSpec { string Method; string Url; Dictionary<string,string> Headers; string? Body; bool IsStream; }
sealed class ApiConfig  { ProtocolKind Kind; string BaseUrl; string ApiKey; string Model; }
sealed class ChatParams { string Message="Hello"; string? System; int MaxTokens=256; double? Temperature; }
sealed class ChatResult { string Text; int? PromptTokens, CompletionTokens, TotalTokens; }
sealed class SseEvent   { string? TextDelta; bool IsDone; int? PromptTokens, CompletionTokens, TotalTokens; }

interface IApiProtocol {
    ProtocolKind Kind { get; }
    string DisplayName { get; }            // 显示在协议下拉
    string DefaultBaseUrl { get; }         // 切换协议自动填
    bool   SupportsTemperature { get; }    // Claude=false
    HttpRequestSpec BuildListModels(ApiConfig cfg);
    List<string>    ParseModelList(string body);
    HttpRequestSpec BuildChat(ApiConfig cfg, ChatParams p, bool stream);
    ChatResult      ParseChatResponse(string body);   // 非流式
    SseEvent        ParseSseEvent(string rawEventBlock); // 一个以空行分隔的 SSE 事件块
}
```

### HTTP 执行
```csharp
sealed class HttpResult { int Status; string StatusText; string Body; long ElapsedMs; long TtftMs;
                          Exception? Error; Dictionary<string,string> Headers;
                          int? PromptTokens, CompletionTokens, TotalTokens;
                          bool IsSuccess; bool Cancelled; }
class ApiClient {
    Task<HttpResult> SendAsync(HttpRequestSpec, CancellationToken);
    Task<HttpResult> StreamAsync(HttpRequestSpec, IApiProtocol,
        Action<long> onFirstByte, Action<string> onDelta, CancellationToken);
}
```
- **SSE 统一**:`StreamAsync` 按"空行分隔成事件块"读取,每块交 `proto.ParseSseEvent`。对 OpenAI 单 `data:` 行、Claude/Responses 的 `event:`+`data:`、Gemini 的 `alt=sse` 都成立。
- 非 2xx **不抛**,读 body(API 错误 JSON)连同状态码返回。
- 流式:首个增量记 TTFT;`onDelta`/`onFirstByte` 在线程池线程触发,UI 侧用 `Dispatcher.InvokeAsync` 包装。

### JSON
- 解析/取值:`System.Text.Json`(`JsonElement` 有 `this[int]` 数组索引器)。
- 美化:`JsonNode.Parse(...).ToJsonString(WriteIndented + UnsafeRelaxedJsonEscaping)`;非 JSON 回退原文。
- 构造请求体:匿名对象 + `JsonSerializer.Serialize`,`WhenWritingNull` 使可空字段(temperature/system 等)为 null 时**不出现在 body**。

### 预设
- `%APPDATA%\ApiTester\presets.json`,数组 `[{Name,Kind,BaseUrl,ApiKey?}]`。
- 勾 "Remember key" 才写明文 Key,否则该字段为 null。
- `PresetBox` 可编辑:输入名 + Save 保存;选下拉项 → 回填。

---

## 9. 四协议完整规格(核心,无损保留)

通用:`BaseUrl` 去尾斜杠;POST 体由 `StringContent(..., "application/json")` 发送;鉴权头用 `TryAddWithoutValidation`。

### OpenAI Chat Completions (`SupportsTemperature=true`,默认 `https://api.openai.com`)
- 列模型:`GET {b}/v1/models`,头 `Authorization: Bearer KEY` → `{"data":[{"id":...}]}`。
- 对话:`POST {b}/v1/chat/completions`,体 `{model, messages:[{role,content}], max_tokens, temperature?, stream}`(有 system 时 messages 首项 `{role:"system",content:sys}`)。
- 非流式:`choices[0].message.content`;用量 `usage.prompt_tokens / completion_tokens / total_tokens`。
- SSE:每块 `data: {...}`,增量 `choices[0].delta.content`,`data: [DONE]` 结束;末尾块可能带 `usage`。

### Claude / Anthropic (`SupportsTemperature=false`,默认 `https://api.anthropic.com`)
- 头:`x-api-key: KEY` + `anthropic-version: 2023-06-01`。
- 列模型:`GET {b}/v1/models` → `{"data":[{"id":"claude-..."}]}`。
- 对话:`POST {b}/v1/messages`,体 `{model, max_tokens(必填!), system?, messages:[{role:"user",content}], stream}`;**刻意不发 temperature**(Opus 4.7/4.8 会 400)。
- 非流式:`content[]` 中 type=="text" 的 `text` 拼接;用量 `usage.input_tokens / output_tokens`(total=两者和)。
- SSE(`event:`+`data:`):`content_block_delta`→`delta.text`;`message_start`→`message.usage.input_tokens`;`message_delta`→`usage.output_tokens`;`message_stop`→结束。

### Google Gemini (`SupportsTemperature=true`,默认 `https://generativelanguage.googleapis.com`)
- Key 走 `?key=KEY`(同时加头 `x-goog-api-key: KEY`)。
- 列模型:`GET {b}/v1beta/models?key=KEY` → `{"models":[{"name":"models/gemini-..."}]}`(**名字带 `models/` 前缀**)。
- 对话:`POST {b}/v1beta/models/{modelId}:generateContent?key=KEY`(流式:`:streamGenerateContent?alt=sse&key=KEY`);**modelId 要去掉 `models/` 前缀**。
- 体:`{contents:[{role:"user",parts:[{text}]}], systemInstruction?:{parts:[{text}]}, generationConfig:{maxOutputTokens, temperature?}}`。
- 提取:`candidates[0].content.parts[*].text`;用量 `usageMetadata.promptTokenCount / candidatesTokenCount / totalTokenCount`。
- SSE:`data: {...}`(无 `[DONE]`,流自然结束)。

### OpenAI Responses / Codex (`SupportsTemperature=true`,默认 `https://api.openai.com`)
- 列模型:同 OpenAI `GET {b}/v1/models`(Bearer)。
- 对话:`POST {b}/v1/responses`,体 `{model, input, instructions?(=system), max_output_tokens, temperature?, stream}`。
- 非流式:优先 `output_text` 便捷字段;否则 `output[]` 中 type=="message" 的 `content[].text` 拼接;用量 `usage.input_tokens / output_tokens / total_tokens`。
- SSE(`event:`+`data:`):`response.output_text.delta`→`delta`;`response.completed`→结束 + `response.usage`;兼容 `data: [DONE]`。

---

## 10. 模型与参数关键事实(来自 claude-api skill,后续 agent 须知)

- 构建/测试 Anthropic 时默认用最新:**Opus 4.8 = `claude-opus-4-8`**;Sonnet 4.6 = `claude-sonnet-4-6`;Haiku 4.5 = `claude-haiku-4-5`。
- Anthropic `anthropic-version` 头当前值 **`2023-06-01`**;`max_tokens` **必填**。
- **Opus 4.8 / 4.7**:`temperature`/`top_p`/`top_k`/`budget_tokens` 都会 **400**(这就是工具对 Claude 协议不发 temperature 的原因)。
- 涉及 Claude/Anthropic API 的问题不要凭记忆——可重新调用 `claude-api` skill 查证。

---

## 11. UI 控件清单(x:Name → 功能)

连接/参数区:`ProtocolBox`(协议下拉,ItemsSource=适配器数组,DisplayMemberPath="DisplayName") · `BaseUrlBox`(初始为空) · `AutoFillUrlBox`(默认不勾;勾选时切换协议自动填该协议默认 URL) · `KeyBox`(**TextBox,明文显示**) · `RememberKeyBox` · `ListModelsBtn` · `ModelBox`(可编辑) · `MaxTokensBox`(默认256) · `TempBox`(Claude 时禁用) · `StreamBox` · `PresetBox`(可编辑) · `SavePresetBtn` · `DelPresetBtn` · `SystemBox` · `MessageBox`(默认"Hello",**注意此字段名与 System.Windows.MessageBox 同名,勿在代码里调用 MessageBox.Show**)。

请求侧:`ShowKeyBox`(显示真实 Key) · `RequestBox`(只读预览) · `SendBtn` · `StopBtn` · `CopyReqBtn`。
响应侧:`ResponseBox`(只读) · `FormatJsonBtn` · `RawRespBtn` · `CopyRespBtn`。
状态栏:`StatusText` · `TimeText` · `TtftText` · `TokensText`。

逻辑要点:`_suspendPreview` 抑制批量改控件时的预览刷新;`Mask`/`MaskUrlKey` 脱敏(保留 `Bearer ` 前缀、URL 的 `key=`);`Finish()` 统一更新状态栏与内容展示;`SetBusy()` 控制按钮可用性。

---

## 12. 验证清单(怎么继续 = 任务 8)

1. `dotnet build`(或封装的 Build.bat)→ 确认无错;`dotnet run` 起窗口、无控制台残留。
2. 逐协议(需各家真实 Key,或 OpenAI 兼容的本地/中转端点免 Key 测 OpenAI 协议):
   - 选协议 → 默认 Base URL 自动填 → 填 Key → **List Models**(模型进下拉)→ 选模型 →
   - **Send Hello**:非流式(响应 + 状态码 + 耗时 + Token);流式(勾 Stream,实时增量 + TTFT)。
   - **Format JSON** 缩进正常;**Raw** 看原始;**Copy** 可复制。
3. 错误路径:故意填错 Key → 状态栏显示 401 + API 错误体。
4. 预设:勾 Remember key + Save → 重开程序 → 选预设回填(含 Key)。
5. 真实响应若与解析不符,按 §9 微调对应适配器的字段路径/SSE 事件名。

---

## 13. 相关文件 / 备注

- 计划文件(已批准,反映 net10/WPF 方案):`C:\Users\John\.claude\plans\cheerful-crunching-otter.md`。
- 项目记忆:`C:\Users\John\.claude\projects\D--Dev-CSharp-APITest\memory\`(`apitest-project.md`、`interact-in-chinese.md`、索引 `MEMORY.md`)。
- "分类器":Claude Code 在 **auto 权限模式**下,执行 Bash/PowerShell 前会做一次"命令是否安全/需否批准"的模型判定;本会话期间该判定调用临时不可用(429),故 agent 自身跑不了命令,但**只读工具(Read/Glob/Write 文件)不受影响**,用户用 `!` 前缀自行执行也不受影响。属平台瞬时问题。
