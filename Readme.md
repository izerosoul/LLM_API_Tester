# LLM API Tester 使用说明

一个测试各家大模型 API 状态的桌面小工具（WPF）。支持 4 种协议：**OpenAI Chat Completions、Claude / Anthropic、Google Gemini、OpenAI Responses (Codex)**。

---

## 一、运行 / 编译

在源码目录 `D:\Dev\CSharp\APITest` 下：

```
Build.bat
```
脚本会用 VS2026 的 MSBuild 直接编译 **Release / .NET Framework 4.8** 版本。输出文件为：

```
bin\Release\ApiTester.exe
```
窗口打开时**自动屏幕居中**。

---

## 二、界面总览

- **顶部 “Connection & Parameters”**：连接信息与请求参数。
- **中部**：左 = **Request (preview)** 发送前请求预览，右 = **Response** 响应；中间竖条可拖动调节左右宽度。
- **底部状态栏**：`Status` / `Time` / `TTFT` / `Tokens`。

---

## 三、快速上手（5 步）

1. 选 **Protocol**（协议）。
2. 填 **Base URL**（接口根地址）。想让它随协议自动填默认地址，就勾右侧 **Auto-fill URL**（见下）。
3. 填 **API Key**（明文显示，方便核对/粘贴）。
4. 点 **List Models** 拉取模型 → 在 **Model** 下拉里选一个。
5. 在 **Message** 写内容（默认 `Hello`），点 **Send**。想看流式输出就勾 **Stream**。

---

## 四、各控件说明

### 顶部
- **Protocol**：协议。切换会清空 Model 列表；选 Claude 时 **Temperature** 会被禁用（该模型不接受 temperature）。
- **Base URL**：API 根地址，如 `https://api.openai.com`。工具会自动补该协议对应的路径（如 `/v1/chat/completions`），你只填到域名即可。
- **Auto-fill URL**（勾选框）：
  - **勾选**：切换协议时自动把 Base URL 填成该协议默认地址（勾上的当下也会立即填当前协议默认）。
  - **不勾（默认）**：切换协议**不动** Base URL——方便你填自己的中转/自建/本地地址。
  - 程序启动时**默认不勾**，因此 **Base URL 初始为空**。
  - 各协议默认地址：OpenAI / Responses = `https://api.openai.com`；Claude = `https://api.anthropic.com`；Gemini = `https://generativelanguage.googleapis.com`。
- **API Key**：密钥，**明文显示**。不同协议鉴权方式不同，工具会自动放到正确位置：OpenAI / Responses → `Authorization: Bearer`；Claude → `x-api-key`（+ `anthropic-version`）；Gemini → URL 的 `?key=`（+ `x-goog-api-key` 头）。
- **Remember key**（勾选框）：见 [六、Remember key 与预设](#六remember-key-与预设重点)。
- **List Models**：用当前 Base URL + Key 拉取模型列表，填进 Model 下拉；列表也会显示在右侧响应框。失败时状态栏/响应框显示错误。
- **List Models Timeout**：List Models 请求超时秒数，默认 5 秒。
- **Model**：模型 ID，可从下拉选，也可手输。
- **Max Output Tokens**：最大生成 token 数（默认 256）。
- **Temperature**：temperature 采样温度（**留空则不发送**）。Claude 协议下禁用。
- **Stream (SSE)**：勾选后走流式，响应实时逐块追加显示，并统计首字耗时 TTFT。
- **Send Timeout**：Send 请求超时秒数，默认 30 秒；流式请求也会按该值限制总耗时。
- **Preset**：连接预设（可编辑下拉）。见第六节。
- **System**：system 提示（可留空）。
- **Message**：要发送的用户消息（默认 `Hello`），支持粘贴多行文本。

### 中部
- **Request (preview)**：发送前**实时预览**将要发出的完整 HTTP 请求包（请求行 + Host + 头 + body），Key 会按真实内容显示。
  - **Send** 发送 · **Stop** 取消进行中的请求 · **Copy** 复制预览文本。
  - **Editable**：勾选后可直接修改 Preview 内容；发送时会按编辑后的 HTTP 包发送。取消勾选后恢复自动生成预览。
- **Response**：响应内容。
  - **Format JSON**：把响应体美化缩进。
  - **Raw**：显示原始响应体（未美化；流式时为原始 SSE 累积）。
  - **Copy**：复制响应文本。

### 底部状态栏
- **Status**：HTTP 状态码 + 文本（或 `Cancelled` / `ERROR`）。
- **Time**：本次请求总耗时（ms）。
- **TTFT**：流式下首块到达耗时（ms）；非流式等于总耗时。
- **Tokens**：用量，格式 `prompt+completion=total`（缺失项显示 `?`）。

---

## 五、非流式 vs 流式

- **非流式**（不勾 Stream）：一次性拿到完整响应，响应框显示**美化后的 JSON**，Token 从响应里解析。
- **流式**（勾 Stream）：响应框**实时追加**文本，状态栏显示 **TTFT**；点 **Raw** 可看原始 SSE。

---

## 六、Remember key 与预设（重点）

**预设（Preset）** = 保存一套界面配置，方便下次一键切换。

- **保存**：在 Preset 框输入一个名字，点 **Save**。
- **加载**：在 Preset 下拉里选中某名字 → 自动回填协议、Base URL、API Key、Model、timeout、token、temperature、stream、system、message 等界面内容。
- **删除**：Preset 框显示某名字时点 **Delete**。
- 切换 Model 时，当前 Preset 会只记住最后选择/输入的模型 ID，不保存完整模型列表。

**Remember key 的作用**：
- Preset 会保存 API Key；这个勾选框只作为界面状态一起保存。

**保存到哪里**：
```
程序所在目录下，与 EXE 同名的 JSON 文件，例如：
bin\Release\ApiTester.json
```
文件是 JSON，形如：
```json
{
  "LastPresetName": "my-openai",
  "Presets": [
    {
      "Name": "my-openai",
      "Kind": 0,
      "BaseUrl": "https://api.openai.com",
      "ApiKey": "sk-xxxx",
      "Model": "gpt-4.1",
      "ListModelsTimeoutSeconds": "5",
      "SendTimeoutSeconds": "30"
    }
  ]
}
```

程序会默认加载上次使用的 Preset。

---

## 七、常见提示

- **401 / 403**：Key 无效或权限不足——状态栏显示状态码，响应框显示 API 的错误说明。
- **429**：被限流，稍后再试。
- **连不上 / 超时**：检查 Base URL、网络、是否需要代理。
- **Claude 与 temperature**：本工具对 Claude 协议**不发送 temperature**（Opus 4.7/4.8 会因此 400）。
- **Gemini 模型名带 `models/` 前缀**：工具会自动处理，选/填都可以。

---

## 八、四协议端点速览

| 协议 | 列模型 | 对话 | 鉴权 |
|---|---|---|---|
| OpenAI Chat | `GET /v1/models` | `POST /v1/chat/completions` | `Authorization: Bearer` |
| Claude | `GET /v1/models` | `POST /v1/messages` | `x-api-key` + `anthropic-version: 2023-06-01` |
| Gemini | `GET /v1beta/models?key=` | `POST /v1beta/models/{model}:generateContent?key=` | `?key=` / `x-goog-api-key` |
| OpenAI Responses | `GET /v1/models` | `POST /v1/responses` | `Authorization: Bearer` |
