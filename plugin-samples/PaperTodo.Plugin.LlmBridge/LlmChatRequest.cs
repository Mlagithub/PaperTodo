using System.Text.Json;

namespace PaperTodo.Plugin.LlmBridge;

/// <summary>
/// Pure prompt/response logic for the LLM bridge. Deliberately free of WPF, host contracts and
/// networking so <c>PaperTodo.LlmBridgeChecks</c> can compile this file standalone.
/// </summary>
internal static class LlmChatRequest
{
    /// <summary>Soft budget for the combined user prompt; guards against notes that exceed common model context windows.</summary>
    internal const int MaximumPromptCharacters = 50_000;

    internal static string BuildEndpoint(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        return trimmed + "/chat/completions";
    }

    /// <summary>Returns a user-facing configuration error, or null when the settings can produce a request.</summary>
    internal static string? ValidateSettings(LlmBridgeSettings settings, bool chinese)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            return LlmBridgeText.For(chinese ? "zh" : null,
                "尚未配置 API Key。请在 插件设置 → LLM Bridge 中填写。",
                "No API key is configured. Fill it in under plugin settings.");
        }

        if (string.IsNullOrWhiteSpace(settings.Model))
        {
            return LlmBridgeText.For(chinese ? "zh" : null,
                "尚未配置模型 ID。请在 插件设置 → LLM Bridge 中填写。",
                "No model ID is configured. Fill it in under plugin settings.");
        }

        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            return LlmBridgeText.For(chinese ? "zh" : null,
                "尚未配置 Base URL。请在 插件设置 → LLM Bridge 中填写。",
                "No base URL is configured. Fill it in under plugin settings.");
        }

        return null;
    }

    internal static string BuildChatRequestBody(
        string model,
        string systemPrompt,
        string userPrompt,
        double temperature)
    {
        var messages = new List<Dictionary<string, string>>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            messages.Add(new Dictionary<string, string> { ["role"] = "system", ["content"] = systemPrompt });
        }

        messages.Add(new Dictionary<string, string> { ["role"] = "user", ["content"] = userPrompt });

        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = false
        };
        // Temperature < 0 (default -1) means "do not send"; some reasoning models reject the field.
        if (temperature >= 0)
        {
            body["temperature"] = temperature;
        }

        return JsonSerializer.Serialize(body);
    }

    internal static string ParseSuccessResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.String)
        {
            var text = content.GetString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        throw new LlmApiException(
            "The API response did not contain a message content field (unexpected response shape).");
    }

    internal static string ParseErrorResponse(int statusCode, string body)
    {
        string? detail = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("message", out var message) &&
                    message.ValueKind == JsonValueKind.String)
                {
                    detail = message.GetString();
                }
                else if (error.ValueKind == JsonValueKind.String)
                {
                    detail = error.GetString();
                }
            }
        }
        catch (JsonException)
        {
        }

        if (string.IsNullOrWhiteSpace(detail))
        {
            detail = body.Trim();
            if (detail.Length > 300)
            {
                detail = detail[..300] + "…";
            }
        }

        return $"HTTP {statusCode}: {detail}";
    }

    internal static string ApplyPromptBudget(string prompt)
    {
        if (prompt.Length <= MaximumPromptCharacters)
        {
            return prompt;
        }

        return prompt[..MaximumPromptCharacters] + "\n\n…[内容过长已截断 / content truncated]";
    }
}

/// <summary>Plain-data description of one todo-driven request, gathered by the Runtime.</summary>
internal sealed record LlmPromptSource(
    string TodoText,
    string PaperTitle,
    string PaperId,
    string TodoId,
    string? LinkedPath,
    string? LinkedPaperTitle,
    string? LinkedPaperId,
    string? LinkedPaperBody,
    bool LinkedPaperBodyAvailable)
{
    internal static LlmPromptSource Empty { get; } = new(
        string.Empty, string.Empty, string.Empty, string.Empty,
        null, null, null, null, LinkedPaperBodyAvailable: false);
}

internal static class LlmPromptBuilder
{
    internal static string BuildTodoPrompt(LlmPromptSource source, bool chinese)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine(source.TodoText.Trim());
        builder.AppendLine();
        builder.AppendLine("[PaperTodo context]");
        builder.Append(LlmBridgeText.For(chinese ? "zh" : null, "来源纸片：", "Source paper: "))
            .AppendLine(source.PaperTitle);
        builder.Append("paper_id: ").AppendLine(source.PaperId);
        builder.Append("todo_id: ").AppendLine(source.TodoId);

        if (!string.IsNullOrWhiteSpace(source.LinkedPath))
        {
            builder.Append(LlmBridgeText.For(chinese ? "zh" : null, "绑定路径：", "Linked path: "))
                .AppendLine(source.LinkedPath);
        }

        if (!string.IsNullOrWhiteSpace(source.LinkedPaperId))
        {
            builder.AppendLine();
            builder.AppendLine(LlmBridgeText.For(chinese ? "zh" : null, "[绑定 PaperTodo 纸片]", "[Linked PaperTodo paper]"));
            builder.Append(LlmBridgeText.For(chinese ? "zh" : null, "标题：", "Title: "))
                .AppendLine(source.LinkedPaperTitle ?? string.Empty);
            builder.Append("paper_id: ").AppendLine(source.LinkedPaperId);
            if (source.LinkedPaperBodyAvailable)
            {
                builder.AppendLine(source.LinkedPaperBody);
            }
            else
            {
                builder.AppendLine(LlmBridgeText.For(
                    chinese ? "zh" : null,
                    "（该纸片正文不通过 Workspace 暴露。）",
                    "(This paper's body is not exposed through Workspace.)"));
            }
        }

        return LlmChatRequest.ApplyPromptBudget(builder.ToString().TrimEnd());
    }

    internal static string BuildPaperPrompt(
        string paperTitle,
        string paperId,
        string paperType,
        string? noteBody,
        bool noteBodyAvailable,
        IReadOnlyList<string> todoLines,
        bool chinese)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine(LlmBridgeText.For(chinese ? "zh" : null, "[PaperTodo 纸片全文]", "[PaperTodo paper contents]"));
        builder.Append(LlmBridgeText.For(chinese ? "zh" : null, "标题：", "Title: ")).AppendLine(paperTitle);
        builder.Append("paper_id: ").AppendLine(paperId);
        builder.AppendLine();

        if (string.Equals(paperType, "todo", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var line in todoLines)
            {
                builder.AppendLine(line);
            }
        }
        else if (string.Equals(paperType, "note", StringComparison.OrdinalIgnoreCase))
        {
            if (noteBodyAvailable)
            {
                builder.AppendLine(noteBody);
            }
            else
            {
                builder.AppendLine(LlmBridgeText.For(
                    chinese ? "zh" : null,
                    "（该 Note 的正文不通过 Workspace 暴露。）",
                    "(This Note's body is not exposed through Workspace.)"));
            }
        }
        else
        {
            builder.AppendLine(LlmBridgeText.For(
                chinese ? "zh" : null,
                "（当前纸片类型没有可导出的正文，只发送标题。）",
                "(This paper type has no exportable body; only the title is sent.)"));
        }

        return LlmChatRequest.ApplyPromptBudget(builder.ToString().TrimEnd());
    }
}

internal static class LlmAnswerFormatter
{
    private const int MaximumTitleCharacters = 24;
    /// <summary>Soft cap for the question text recorded inside the answer note.</summary>
    internal const int MaximumRecordedQuestionCharacters = 4000;

    /// <summary>Appended to an existing linked note: one Q/A section separated by a rule.</summary>
    internal static string FormatAnswerAppend(
        string question,
        string model,
        string answer,
        string timeStampText,
        bool chinese)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine();
        builder.AppendLine("---");
        builder.AppendLine();
        builder.Append(LlmBridgeText.For(chinese ? "zh" : null, "**问（", "**Q ("))
            .Append(model)
            .Append(LlmBridgeText.For(chinese ? "zh" : null, " · ", " · "))
            .Append(timeStampText)
            .AppendLine(LlmBridgeText.For(chinese ? "zh" : null, "）：**", "): **"));
        builder.AppendLine(question.Trim());
        builder.AppendLine();
        builder.AppendLine(LlmBridgeText.For(chinese ? "zh" : null, "**答：**", "**A:**"));
        builder.Append(answer.Trim());
        return builder.ToString();
    }

    /// <summary>Content of a brand-new answer note.</summary>
    internal static string FormatNewNoteContent(
        string question,
        string model,
        string answer,
        string timeStampText,
        bool chinese)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(LlmBridgeText.For(chinese ? "zh" : null, "**问（", "**Q ("))
            .Append(model)
            .Append(LlmBridgeText.For(chinese ? "zh" : null, " · ", " · "))
            .Append(timeStampText)
            .AppendLine(LlmBridgeText.For(chinese ? "zh" : null, "）：**", "): **"));
        builder.AppendLine(question.Trim());
        builder.AppendLine();
        builder.AppendLine(LlmBridgeText.For(chinese ? "zh" : null, "**答：**", "**A:**"));
        builder.AppendLine();
        builder.Append(answer.Trim());
        return builder.ToString();
    }

    /// <summary>
    /// The question text recorded in the answer note for a paper-level send: what was actually
    /// asked (the todo list or the note body), not just the paper title. Long content is capped.
    /// </summary>
    internal static string BuildPaperQuestion(
        string paperTitle,
        string paperType,
        IReadOnlyList<string> todoLines,
        string? noteBody,
        bool noteBodyAvailable,
        bool chinese)
    {
        string question;
        if (string.Equals(paperType, "todo", StringComparison.OrdinalIgnoreCase))
        {
            question = string.Join("\n", todoLines);
            if (string.IsNullOrWhiteSpace(question))
            {
                question = LlmBridgeText.For(chinese ? "zh" : null, "（清单为空）", "(empty list)");
            }
        }
        else if (string.Equals(paperType, "note", StringComparison.OrdinalIgnoreCase))
        {
            question = noteBodyAvailable && !string.IsNullOrWhiteSpace(noteBody)
                ? noteBody
                : LlmBridgeText.For(chinese ? "zh" : null, "（无正文）", "(no body)");
        }
        else
        {
            question = LlmBridgeText.For(chinese ? "zh" : null, "（该纸片类型没有可导出的正文）", "(no exportable body)");
        }

        question = question.Trim();
        if (question.Length > MaximumRecordedQuestionCharacters)
        {
            question = question[..MaximumRecordedQuestionCharacters].TrimEnd() +
                       "\n…[" + LlmBridgeText.For(chinese ? "zh" : null, "问题内容过长已截断", "question truncated") + "]";
        }

        return question;
    }

    /// <summary>Title for a newly created answer note, derived from the todo text or paper title.</summary>
    internal static string BuildAnswerNoteTitle(string sourceText, bool chinese)
    {
        var text = (sourceText ?? string.Empty).Trim();
        var newLine = text.IndexOfAny(['\r', '\n']);
        if (newLine >= 0)
        {
            text = text[..newLine].Trim();
        }

        if (text.Length > MaximumTitleCharacters)
        {
            text = text[..MaximumTitleCharacters].TrimEnd() + "…";
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return LlmBridgeText.For(chinese ? "zh" : null, "LLM 回答", "LLM answer");
        }

        return LlmBridgeText.For(chinese ? "zh" : null, "回答：", "Answer: ") + text;
    }
}

/// <summary>Thrown when the remote endpoint answered but not with usable completion content.</summary>
internal sealed class LlmApiException : Exception
{
    public LlmApiException(string message) : base(message)
    {
    }
}

internal sealed record LlmRequestLogEntry(
    string Time,
    string Source,
    string Status,
    string Detail);

/// <summary>Provider-scoped backend state: a bounded log of recent bridge requests.</summary>
internal sealed record LlmRuntimeState(IReadOnlyList<LlmRequestLogEntry> Requests)
{
    private const int MaximumEntries = 20;
    private const int MaximumDetailCharacters = 200;

    internal static LlmRuntimeState Read(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new LlmRuntimeState([]);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("requests", out var requests) ||
                requests.ValueKind != JsonValueKind.Array)
            {
                return new LlmRuntimeState([]);
            }

            var entries = new List<LlmRequestLogEntry>();
            foreach (var item in requests.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                entries.Add(new LlmRequestLogEntry(
                    Text(item, "time"),
                    Text(item, "source"),
                    Text(item, "status"),
                    Text(item, "detail")));
            }

            return new LlmRuntimeState(entries);
        }
        catch (JsonException)
        {
            // A malformed state file must not wipe the runtime; start with an empty log.
            return new LlmRuntimeState([]);
        }
    }

    internal LlmRuntimeState With(LlmRequestLogEntry entry)
    {
        var detail = entry.Detail ?? string.Empty;
        if (detail.Length > MaximumDetailCharacters)
        {
            detail = detail[..MaximumDetailCharacters] + "…";
        }

        var entries = new List<LlmRequestLogEntry>(Requests) { entry with { Detail = detail } };
        if (entries.Count > MaximumEntries)
        {
            entries.RemoveRange(0, entries.Count - MaximumEntries);
        }

        return new LlmRuntimeState(entries);
    }

    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    internal string Serialize() => JsonSerializer.Serialize(this, SerializerOptions);

    private static string Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
