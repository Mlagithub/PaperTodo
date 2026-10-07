# LLM Bridge（LLM 桥接）

把 PaperTodo 的待办和纸片内容一键发送给**任意 OpenAI 兼容大模型**（`baseURL + api_key`），回答自动写入笔记。这是一个无界面纯桥接插件：没有聊天窗口，一切围绕你已有的待办和笔记运转。

> Sends PaperTodo todos and paper content to any OpenAI-compatible LLM (`baseURL + api_key`) and writes the answer into a note. Headless by design — no chat window.

## 使用方式

- **待办级**：任意待办行内会出现 `AI` 按钮（右键菜单也有"问 LLM"）。点击后把**待办文本 + 来源纸片 + 关联纸片内容**一起发给模型。
- **纸片级**：任意纸片顶栏的 `AI` 按钮（插件启用后出现）会把**整张纸片**（待办清单或笔记全文）发给模型。

### 回答落盘规则

| 场景 | 行为 |
| --- | --- |
| 待办已关联笔记 | 追加一节 `问（模型 · 时间）→ 答` Markdown 段，并显示该笔记 |
| 待办无关联（或关联的是待办纸） | 新建一张内置 Markdown 笔记（标题取待办文本），写问答内容；原本无关联的待办自动回链 |
| 纸片级发送 | 总是新建一张"回答：xxx"笔记 |
| 请求失败 | 目标笔记里写入 `⚠️ 请求失败：原因`（尽量保留现场） |
| 配置缺失（Key/模型/URL） | 写入/追加固定的"LLM Bridge 配置问题"笔记，并记录到隐藏插件纸片的最近请求日志 |

## 配置

| 设置 | 默认值 | 说明 |
| --- | --- | --- |
| Base URL | `https://api.openai.com/v1` | OpenAI 兼容 API 根地址，自动拼接 `/chat/completions` |
| API Key | 空 | 以 `Authorization: Bearer` 发送 |
| Model | `gpt-4o-mini` | 每次请求携带的模型 ID |
| 请求超时 | 240 秒 | 推理模型可调大（30–900） |
| 启动后自动启用 | 开 | 创建隐藏插件纸片，保持全局按钮可用 |
| 系统提示词 | 空 | 每次请求的 system 消息 |
| Temperature | -1 | **-1 = 请求不携带该字段**（推理模型推荐）；0–2 正常发送 |

### 常见服务示例

| 服务 | Base URL | Model 示例 |
| --- | --- | --- |
| OpenAI | `https://api.openai.com/v1` | `gpt-4o-mini` |
| DeepSeek | `https://api.deepseek.com/v1` | `deepseek-chat` |
| Kimi（月之暗面） | `https://api.moonshot.cn/v1` | `moonshot-v1-8k` |
| 智谱 GLM | `https://open.bigmodel.cn/api/paas/v4` | `glm-4.7` |
| 通义千问（兼容模式） | `https://dashscope.aliyuncs.com/compatible-mode/v1` | `qwen-plus` |
| Ollama（本地） | `http://localhost:11434/v1` | `qwen3:8b` |
| vLLM / one-api / 各类中转 | 按服务文档 | 按服务文档 |

> Anthropic 官方 API 不是 OpenAI 兼容协议；如需使用 Claude，请通过兼容网关（如各类 proxy/one-api 部署）接入。

## 构建、安装与卸载

```powershell
# 关闭 PaperTodo 后执行；-ProjectPath 指向 .csproj 文件（不是目录）
powershell -File plugin-samples/Build-And-Install-NativePlugin.ps1 `
  -ProjectPath plugin-samples/PaperTodo.Plugin.LlmBridge/PaperTodo.Plugin.LlmBridge.csproj
```

产物安装到 `plugins/tools.llm-bridge.native/`，重启 PaperTodo 生效。修改插件文件后同样需要重启（无热重载）。卸载直接删除该目录及 `plugins/data/tools.llm-bridge.native.json`。

## 安全说明

- API Key 与系统提示词由宿主 `PaperBodyPluginDataStore` 明文保存在 `plugins/data/tools.llm-bridge.native.json`。
- 插件声明的权限：读取纸片/待办/笔记（含事件观察）、追加笔记、创建笔记纸、更新待办关联。它不会修改已有待办文本、不会删除任何内容。
- 提示词对笔记正文做了 50,000 字符软截断，超出部分以截断标记结尾。

## 离线逻辑检查

请求构建、响应解析、设置读取等纯逻辑由 `tests/PaperTodo.LlmBridgeChecks` 覆盖：

```bash
dotnet run --project tests/PaperTodo.LlmBridgeChecks
```
