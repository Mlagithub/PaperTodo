using System.Text.Json;
using PaperTodo.Plugin.LlmBridge;

var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}

LlmBridgeSettings Parse(string json) => LlmBridgeSettings.Read(json);

// --- Settings defaults: empty and missing documents fall back to manifest defaults.
// Settings JSON is host-produced (manifest-normalized), so malformed documents throw like
// CodexCliBridge; the Runtime's own catch reports them as request failures. ---
foreach (var json in new[] { "", "   ", "{}" })
{
    var settings = Parse(json);
    Check(settings.BaseUrl == LlmBridgeSettings.DefaultBaseUrl, $"Base URL default: {json}");
    Check(settings.ApiKey == string.Empty, $"API key default: {json}");
    Check(settings.Model == LlmBridgeSettings.DefaultModel, $"Model default: {json}");
    Check(settings.SystemPrompt == string.Empty, $"System prompt default: {json}");
    Check(settings.Temperature == LlmBridgeSettings.TemperatureDisabled, $"Temperature default: {json}");
    Check(settings.TimeoutSeconds == LlmBridgeSettings.DefaultTimeoutSeconds, $"Timeout default: {json}");
    Check(settings.AutoStart, $"AutoStart default: {json}");
}

CheckThrows(() => Parse("not json at all"), "Malformed settings JSON must throw for the caller to report.");
CheckThrows(() => Parse("[1,2,3]"), "Wrong-shape settings JSON must throw.");
CheckThrows(() => Parse("null"), "Non-object settings JSON must throw.");

// --- Settings parsing: strings trim, numbers tolerate, booleans only trust JSON true. ---
var full = Parse("""
{
  "baseUrl": " https://api.deepseek.com/v1 ",
  "apiKey": "  sk-test  ",
  "model": "deepseek-chat",
  "systemPrompt": "回答保持简短",
  "temperature": 0.5,
  "timeoutSeconds": 300,
  "autoStart": true
}
""");
Check(full.BaseUrl == "https://api.deepseek.com/v1", "Base URL trims.");
Check(full.ApiKey == "sk-test", "API key trims.");
Check(full.Temperature == 0.5, "Temperature parses.");
Check(full.TimeoutSeconds == 300, "Timeout parses.");
Check(full.AutoStart, "AutoStart true.");

var hostile = Parse("""
{
  "baseUrl": 42,
  "apiKey": null,
  "model": [],
  "temperature": "0.5",
  "timeoutSeconds": "300",
  "autoStart": 1
}
""");
Check(hostile.BaseUrl == LlmBridgeSettings.DefaultBaseUrl, "Non-string Base URL falls back.");
Check(hostile.ApiKey == string.Empty, "Null API key falls back.");
Check(hostile.Model == LlmBridgeSettings.DefaultModel, "Non-string model falls back.");
Check(hostile.Temperature == LlmBridgeSettings.TemperatureDisabled, "Non-number temperature falls back.");
Check(hostile.TimeoutSeconds == LlmBridgeSettings.DefaultTimeoutSeconds, "Non-number timeout falls back.");
// Only JSON true counts as enabled (the CodexCliBridge contract): a present-but-not-true
// value disables regardless of the default, so a malformed write can never opt the user in.
Check(!hostile.AutoStart, "Present-but-not-true autoStart disables.");
Check(Parse("{}").AutoStart, "Missing autoStart keeps the default.");

var explicitOff = Parse("{\"autoStart\": false}");
Check(!explicitOff.AutoStart, "A saved opt-out must persist.");

// --- ValidateSettings: clear errors for missing key/model, success once both exist. ---
var noKey = LlmChatRequest.ValidateSettings(Parse("{}"), chinese: true);
Check(noKey != null && noKey.Contains("API Key"), "Missing key is reported.");
var noModel = LlmChatRequest.ValidateSettings(
    Parse("{}") with { ApiKey = "sk-x", Model = "" }, chinese: false);
Check(noModel != null && noModel.Contains("model", StringComparison.OrdinalIgnoreCase),
    "Missing model is reported.");
var ok = LlmChatRequest.ValidateSettings(
    Parse("{}") with { ApiKey = "sk-x", Model = "gpt-4o-mini" }, chinese: true);
Check(ok == null, "Valid connection settings pass.");
var noUrl = LlmChatRequest.ValidateSettings(
    Parse("{}") with { ApiKey = "sk-x", Model = "m", BaseUrl = "" }, chinese: false);
Check(noUrl != null, "Empty base URL cannot produce a request.");

// --- Endpoint building: trailing slashes are trimmed, path appended once. ---
Check(LlmChatRequest.BuildEndpoint("https://api.openai.com/v1") == "https://api.openai.com/v1/chat/completions",
    "Plain base URL.");
Check(LlmChatRequest.BuildEndpoint("https://api.openai.com/v1/") == "https://api.openai.com/v1/chat/completions",
    "Trailing slash is trimmed.");
Check(LlmChatRequest.BuildEndpoint(" http://localhost:11434/v1 ") == "http://localhost:11434/v1/chat/completions",
    "Whitespace is trimmed.");

// --- Request body: messages, system prompt and the temperature contract. ---
var body = JsonDocument.Parse(LlmChatRequest.BuildChatRequestBody("m1", "", "hi", -1)).RootElement;
Check(body.GetProperty("model").GetString() == "m1", "Model is sent.");
Check(body.GetProperty("stream").GetBoolean() == false, "Stream is off.");
Check(!body.TryGetProperty("temperature", out _), "Temperature -1 omits the field.");
Check(body.GetProperty("messages").GetArrayLength() == 1, "No system message when prompt is empty.");
Check(body.GetProperty("messages")[0].GetProperty("role").GetString() == "user", "User message role.");

var bodyWithSystem = JsonDocument.Parse(
    LlmChatRequest.BuildChatRequestBody("m1", "be brief", "hi", 0.7)).RootElement;
var messages = bodyWithSystem.GetProperty("messages");
Check(messages.GetArrayLength() == 2, "System prompt prepends a message.");
Check(messages[0].GetProperty("role").GetString() == "system", "System role.");
Check(messages[0].GetProperty("content").GetString() == "be brief", "System content.");
Check(messages[1].GetProperty("role").GetString() == "user", "User message second.");
Check(Math.Abs(bodyWithSystem.GetProperty("temperature").GetDouble() - 0.7) < 1e-9, "Temperature 0.7 is sent.");

// --- Success parsing: choices[0].message.content, or a clear failure. ---
var answer = LlmChatRequest.ParseSuccessResponse(
    """{"choices":[{"message":{"role":"assistant","content":"  hello  "}}]}""");
Check(answer == "  hello  ", "Content is returned as-is.");
CheckThrows(() => LlmChatRequest.ParseSuccessResponse("""{"choices":[]}"""), "Empty choices must throw.");
CheckThrows(() => LlmChatRequest.ParseSuccessResponse("""{"error":{"message":"x"}}"""), "Error body must throw.");
CheckThrows(() => LlmChatRequest.ParseSuccessResponse(
    """{"choices":[{"message":{"content":""}}]}"""), "Empty content must throw.");

// --- Error parsing: OpenAI shape, string error, plain body, long-body truncation. ---
Check(LlmChatRequest.ParseErrorResponse(401, """{"error":{"message":"bad key"}}""") == "HTTP 401: bad key",
    "OpenAI error message is extracted.");
Check(LlmChatRequest.ParseErrorResponse(429, """{"error":"rate limited"}""") == "HTTP 429: rate limited",
    "String error is extracted.");
Check(LlmChatRequest.ParseErrorResponse(500, "<html>oops</html>") == "HTTP 500: <html>oops</html>",
    "Plain body is preserved.");
var longBody = new string('x', 400);
var truncated = LlmChatRequest.ParseErrorResponse(500, longBody);
Check(truncated.Length < longBody.Length && truncated.EndsWith('…'), "Long bodies are truncated.");

void CheckThrows(Action action, string message)
{
    try
    {
        action();
    }
    catch (Exception)
    {
        checks++;
        return;
    }

    throw new InvalidOperationException(message);
}

// --- Todo prompt building: context header, linked path and linked paper body. ---
var source = new LlmPromptSource(
    TodoText: "整理测试目录",
    PaperTitle: "开发任务",
    PaperId: "paper-1",
    TodoId: "todo-1",
    LinkedPath: @"D:\repo",
    LinkedPaperTitle: "背景说明",
    LinkedPaperId: "paper-2",
    LinkedPaperBody: "# 背景\n这里是一段笔记。",
    LinkedPaperBodyAvailable: true);
var todoPrompt = LlmPromptBuilder.BuildTodoPrompt(source, chinese: true);
Check(todoPrompt.StartsWith("整理测试目录", StringComparison.Ordinal), "Todo text leads the prompt.");
Check(todoPrompt.Contains("paper_id: paper-1", StringComparison.Ordinal), "Source paper id included.");
Check(todoPrompt.Contains("todo_id: todo-1", StringComparison.Ordinal), "Todo id included.");
Check(todoPrompt.Contains(@"D:\repo", StringComparison.Ordinal), "Linked path included.");
Check(todoPrompt.Contains("paper_id: paper-2", StringComparison.Ordinal), "Linked paper id included.");
Check(todoPrompt.Contains("# 背景", StringComparison.Ordinal), "Linked note body included.");

var withoutBody = source with { LinkedPaperBodyAvailable = false };
Check(LlmPromptBuilder.BuildTodoPrompt(withoutBody, chinese: false)
        .Contains("not exposed through Workspace", StringComparison.Ordinal),
    "Unavailable body explains itself.");

var minimal = new LlmPromptSource(
    "任务", "纸", "p1", "t1", null, null, null, null, LinkedPaperBodyAvailable: false);
Check(!LlmPromptBuilder.BuildTodoPrompt(minimal, chinese: true).Contains("绑定 PaperTodo 纸片", StringComparison.Ordinal),
    "No linked-paper section without a link.");

// --- Prompt budget: oversized content is truncated with a marker. ---
var huge = new LlmPromptSource(
    new string('x', LlmChatRequest.MaximumPromptCharacters + 10),
    "纸", "p1", "t1", null, null, null, null, LinkedPaperBodyAvailable: false);
var hugePrompt = LlmPromptBuilder.BuildTodoPrompt(huge, chinese: true);
Check(hugePrompt.Length < LlmChatRequest.MaximumPromptCharacters + 200, "Budgeted prompt stays bounded.");
Check(hugePrompt.Contains("内容过长已截断", StringComparison.Ordinal), "Truncation is announced.");

// --- Paper prompt building: todo list, note body and unknown types. ---
var todoPaperPrompt = LlmPromptBuilder.BuildPaperPrompt(
    "清单", "p1", "todo", null, false, ["- [x] done", "- [ ] open"], chinese: true);
Check(todoPaperPrompt.Contains("- [x] done", StringComparison.Ordinal), "Todo lines are exported.");
Check(todoPaperPrompt.Contains("- [ ] open", StringComparison.Ordinal), "Open todos are exported.");

var notePaperPrompt = LlmPromptBuilder.BuildPaperPrompt(
    "笔记", "p1", "note", "# 正文", true, [], chinese: false);
Check(notePaperPrompt.Contains("# 正文", StringComparison.Ordinal), "Note body is exported.");

var lockedNotePrompt = LlmPromptBuilder.BuildPaperPrompt(
    "笔记", "p1", "note", null, false, [], chinese: true);
Check(lockedNotePrompt.Contains("不通过 Workspace 暴露", StringComparison.Ordinal),
    "Unavailable note body explains itself.");

var unknownPrompt = LlmPromptBuilder.BuildPaperPrompt(
    "插件纸", "p1", "plugin", null, false, [], chinese: true);
Check(unknownPrompt.Contains("只发送标题", StringComparison.Ordinal), "Unknown types degrade to title-only.");

// --- Answer formatting: append section, new note content and title building. ---
var append = LlmAnswerFormatter.FormatAnswerAppend("问题", "m1", "回答内容", "2026-10-07 14:30", chinese: true);
Check(append.Contains("---", StringComparison.Ordinal), "Append starts with a rule.");
Check(append.Contains("**问（m1 · 2026-10-07 14:30）：**", StringComparison.Ordinal), "Chinese question header.");
Check(append.Contains("**答：**", StringComparison.Ordinal), "Chinese answer header.");
Check(append.EndsWith("回答内容", StringComparison.Ordinal), "Answer body is preserved.");

var appendEn = LlmAnswerFormatter.FormatAnswerAppend("q", "m1", "a", "2026-10-07 14:30", chinese: false);
Check(appendEn.Contains("**Q (m1 · 2026-10-07 14:30): **", StringComparison.Ordinal), "English question header.");
Check(appendEn.Contains("**A:**", StringComparison.Ordinal), "English answer header.");

var newNote = LlmAnswerFormatter.FormatNewNoteContent("问题", "m1", "回答内容", "2026-10-07 14:30", chinese: true);
Check(newNote.Contains("**问（", StringComparison.Ordinal) && newNote.Contains("**答：**", StringComparison.Ordinal),
    "New note content carries both headers.");

Check(LlmAnswerFormatter.BuildAnswerNoteTitle("第一行\r\n第二行", chinese: true) == "回答：第一行",
    "Title uses the first line.");
Check(LlmAnswerFormatter.BuildAnswerNoteTitle(new string('长', 40), chinese: true).Length == "回答：".Length + 24 + 1,
    "Long titles are truncated to 24 characters plus an ellipsis.");
Check(LlmAnswerFormatter.BuildAnswerNoteTitle("", chinese: false) == "LLM answer", "Empty text falls back.");

// --- Paper question recording: the 问 section carries what was actually sent. ---
var questionFromList = LlmAnswerFormatter.BuildPaperQuestion(
    "清单", "todo", ["- [x] done", "- [ ] open"], null, false, chinese: true);
Check(questionFromList == "- [x] done\n- [ ] open", "Todo-paper question is the joined list.");

var emptyListQuestion = LlmAnswerFormatter.BuildPaperQuestion(
    "清单", "todo", [], null, false, chinese: true);
Check(emptyListQuestion == "（清单为空）", "Empty todo list explains itself.");

Check(LlmAnswerFormatter.BuildPaperQuestion("笔记", "note", [], "# 正文", true, chinese: false) == "# 正文",
    "Note-paper question is the note body.");
Check(LlmAnswerFormatter.BuildPaperQuestion("笔记", "note", [], null, false, chinese: true) == "（无正文）",
    "Unavailable note body explains itself.");
Check(LlmAnswerFormatter.BuildPaperQuestion("插件纸", "plugin", [], null, false, chinese: false) ==
      "(no exportable body)",
    "Unknown types degrade to a marker.");

var longQuestion = LlmAnswerFormatter.BuildPaperQuestion(
    "笔记", "note", [],
    new string('x', LlmAnswerFormatter.MaximumRecordedQuestionCharacters + 50), true, chinese: true);
Check(longQuestion.Length < LlmAnswerFormatter.MaximumRecordedQuestionCharacters + 100,
    "Recorded question stays bounded.");
Check(longQuestion.Contains("问题内容过长已截断", StringComparison.Ordinal), "Question truncation is announced.");

// --- Runtime state: bounded log with tolerant parsing and stable round-trip. ---
Check(LlmRuntimeState.Read("").Requests.Count == 0, "Empty state starts clean.");
Check(LlmRuntimeState.Read("{{{").Requests.Count == 0, "Malformed state starts clean.");
Check(LlmRuntimeState.Read("""{"other":true}""").Requests.Count == 0, "Foreign state starts clean.");

var state = LlmRuntimeState.Read("");
for (var i = 0; i < 25; i++)
{
    state = state.With(new LlmRequestLogEntry($"0{i}", "src", "ok", new string('d', 300) + i));
}

Check(state.Requests.Count == 20, "Log is capped at 20 entries.");
Check(state.Requests[^1].Detail.Length == 201, "Detail is truncated to 200 plus an ellipsis.");
Check(state.Requests[^1].Detail.EndsWith('…'), "Detail truncation marker.");
var restored = LlmRuntimeState.Read(state.Serialize());
Check(restored.Requests.Count == state.Requests.Count, "Round-trip preserves entries.");
Check(restored.Requests[^1].Time == state.Requests[^1].Time, "Round-trip preserves fields.");

// --- Text helper: language routing follows the zh prefix. ---
Check(LlmBridgeText.IsChinese("zh-CN"), "zh-CN is Chinese.");
Check(LlmBridgeText.IsChinese("zh-Hans"), "zh-Hans is Chinese.");
Check(!LlmBridgeText.IsChinese("en-US"), "en-US is not Chinese.");
Check(!LlmBridgeText.IsChinese(""), "Empty is not Chinese.");
Check(LlmBridgeText.For("zh-CN", "甲", "乙") == "甲", "Chinese routes to zh.");
Check(LlmBridgeText.For("en-US", "甲", "乙") == "乙", "Other languages route to en.");

Console.WriteLine($"PaperTodo.LlmBridgeChecks: {checks} checks passed.");
