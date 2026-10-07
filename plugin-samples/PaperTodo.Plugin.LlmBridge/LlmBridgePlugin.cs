using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.LlmBridge;

public sealed class LlmBridgePlugin : IPaperBodyPlugin, IPaperPluginRuntimeProvider
{
    public IPaperBodySession Create(PaperBodyContext context) => new Session(context);

    public IPaperPluginRuntime CreatePluginRuntime(PaperPluginRuntimeContext context) => new Runtime(context);

    /// <summary>
    /// The startup paper is hidden (pure bridge, no chat UI). Expanding it shows a plain info page
    /// describing the bridge and the recent request log kept by the Runtime.
    /// </summary>
    private sealed class Session : IPaperBodySession
    {
        private readonly PaperBodyContext _context;
        private readonly string _uiLanguage;
        private readonly TextBlock _title;
        private readonly TextBlock _description;
        private readonly TextBlock _status;
        private bool _disposed;

        public Session(PaperBodyContext context)
        {
            _context = context;
            _uiLanguage = context.UiLanguage;

            _title = new TextBlock
            {
                Text = "LLM Bridge",
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            };
            _description = new TextBlock
            {
                Text = T(
                    "这是一个无界面桥接纸片。在任意待办上点击 AI 按钮（或右键选择“问 LLM”）即可把该待办及其关联内容发送给模型；在任意纸片顶栏点击 AI 按钮则发送整张纸片。回答会追加到待办关联的笔记，没有关联时自动创建一张笔记并回链。模型、Base URL 与 API Key 在插件设置中配置。",
                    "This is a headless bridge paper. Click the AI button on any todo (or use the context menu) to send that todo and its linked content to the model; the AI top-bar button sends the whole current paper. Answers are appended to the linked note, or written to a new note that is linked back to the todo. Configure model, base URL and API key in the plugin settings."),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
                Margin = new Thickness(0, 0, 0, 10)
            };
            _status = new TextBlock
            {
                Margin = new Thickness(0, 8, 0, 0),
                Text = T("正在读取最近请求…", "Loading recent requests…"),
                TextWrapping = TextWrapping.Wrap
            };

            View = new StackPanel
            {
                Margin = new Thickness(18),
                Children = { _title, _description, _status }
            };

            ApplyTheme(context.Body.Theme);
        }

        public FrameworkElement View { get; }

        public bool OnRuntimeMessage(JsonElement message)
        {
            if (_disposed ||
                message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("type", out var typeValue))
            {
                return false;
            }

            if (!string.Equals(typeValue.GetString(), "status", StringComparison.Ordinal))
            {
                return false;
            }

            var lines = new List<string>();
            if (message.TryGetProperty("lines", out var linesValue) &&
                linesValue.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in linesValue.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        lines.Add(item.GetString() ?? string.Empty);
                    }
                }
            }

            _status.Text = lines.Count == 0
                ? T("暂无请求记录。", "No requests yet.")
                : string.Join(Environment.NewLine, lines);
            return true;
        }

        public void OnActivated() => RequestStatus();

        private void RequestStatus()
        {
            if (_disposed)
            {
                return;
            }

            var sent = _context.Runtime.Post(JsonSerializer.SerializeToElement(new
            {
                type = "getStatus"
            }));
            if (!sent)
            {
                _status.Text = T(
                    "Runtime 暂不可用；重新打开这张纸后会再次读取。",
                    "The runtime is unavailable; reopen this paper to try again.");
            }
        }

        public void OnThemeChanged(PaperBodyTheme theme) => ApplyTheme(theme);

        public void OnTypographyChanged(PaperBodyTheme theme) => ApplyTheme(theme);

        public void Dispose() => _disposed = true;

        private void ApplyTheme(PaperBodyTheme theme)
        {
            var text = BrushFrom(theme.TextColor, Brushes.Black);
            var weak = BrushFrom(theme.WeakTextColor, Brushes.Gray);

            _title.Foreground = text;
            _description.Foreground = weak;
            _status.Foreground = weak;
            _description.FontFamily = new FontFamily(theme.FontFamily);
            _description.FontSize = 14 * Math.Clamp(theme.FontScale, 0.85, 1.3);
            _status.FontFamily = new FontFamily(theme.FontFamily);
            _status.FontSize = 13 * Math.Clamp(theme.FontScale, 0.85, 1.3);
        }

        private string T(string zh, string en) => LlmBridgeText.For(_uiLanguage, zh, en);

        private static Brush BrushFrom(string value, Brush fallback)
        {
            try
            {
                return new BrushConverter().ConvertFromString(value) as Brush ?? fallback;
            }
            catch
            {
                return fallback;
            }
        }
    }

    private sealed class Runtime : IPaperPluginRuntime
    {
        private const string TodoActionId = "ask-llm-todo";
        private const string TodoContextActionId = "ask-llm-todo-menu";
        private const string TopBarActionId = "ask-llm-paper";

        private readonly PaperPluginRuntimeContext _context;
        private readonly string _uiLanguage;
        private readonly bool _chinese;
        private readonly PaperTodoAction[] _todoActions;
        private readonly IDisposable _workspaceSubscription;
        private readonly IDisposable _runtimePaperSubscription;
        private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cancellation = new();
        private LlmRuntimeState _state;
        private bool _disposed;

        public Runtime(PaperPluginRuntimeContext context)
        {
            _context = context;
            _uiLanguage = context.UiLanguage;
            _chinese = LlmBridgeText.IsChinese(_uiLanguage);
            _state = LlmRuntimeState.Read(context.State.Json);
            _todoActions =
            [
                new PaperTodoAction
                {
                    Id = TodoActionId,
                    Icon = PaperTopBarIcon.Character("AI"),
                    Text = "LLM",
                    ToolTip = T("发送到大模型，回答写入笔记", "Send to the LLM; the answer is written to a note"),
                    Priority = 80,
                    Placement = PaperTodoActionPlacement.Inline
                },
                new PaperTodoAction
                {
                    Id = TodoContextActionId,
                    Icon = PaperTopBarIcon.Character("AI"),
                    Text = T("问 LLM", "Ask LLM"),
                    ToolTip = T("发送到大模型，回答写入笔记", "Send to the LLM; the answer is written to a note"),
                    Priority = 80,
                    Placement = PaperTodoActionPlacement.ContextMenu
                }
            ];

            context.TodoActions.SetActionHandler(OnTodoAction);
            context.GlobalTopBar.SetActionHandler(OnTopBarAction);
            context.GlobalTopBar.SetActions(
            [
                new PaperTopBarAction
                {
                    Id = TopBarActionId,
                    Icon = PaperTopBarIcon.Character("AI"),
                    ToolTip = T("把当前纸片发送给大模型，回答写入新笔记", "Send the current paper to the LLM; the answer is written to a new note"),
                    Priority = 80
                }
            ]);

            PublishTodoActions();
            _workspaceSubscription = context.Workspace.Subscribe(
                new PaperTodoEventFilter
                {
                    Kinds = new HashSet<PaperTodoEventKind>
                    {
                        PaperTodoEventKind.TodoCreated,
                        PaperTodoEventKind.TodoChanged,
                        PaperTodoEventKind.TodoDeleted
                    },
                    ExcludeOwnOperations = false
                },
                OnWorkspaceEvent);
            _runtimePaperSubscription = context.Papers.Subscribe(OnRuntimePaperEvent);
        }

        private void PublishTodoActions()
        {
            foreach (var todo in _context.Workspace.ListTodos(includeBlank: false))
            {
                PublishTodoAction(todo);
            }
        }

        private void PublishTodoAction(TodoSnapshot todo)
        {
            if (_disposed)
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(todo.Text))
            {
                _context.TodoActions.Clear(todo.PaperId, todo.Id);
                return;
            }
            _context.TodoActions.SetActions(todo.PaperId, todo.Id, _todoActions);
        }

        private void OnWorkspaceEvent(PaperTodoEvent value)
        {
            if (_disposed)
            {
                return;
            }

            switch (value)
            {
                case TodoCreatedEvent created:
                    PublishTodoAction(created.Todo);
                    break;
                case TodoChangedEvent changed:
                    PublishTodoAction(changed.After);
                    break;
                case TodoDeletedEvent deleted:
                    _context.TodoActions.Clear(deleted.Todo.PaperId, deleted.Todo.Id);
                    break;
            }
        }

        private void OnRuntimePaperEvent(PaperPluginRuntimeEvent value)
        {
            if (_disposed ||
                value.Kind != PaperPluginRuntimeEventKind.Message ||
                value.Message is not JsonElement message ||
                message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("type", out var typeValue))
            {
                return;
            }

            if (string.Equals(typeValue.GetString(), "getStatus", StringComparison.Ordinal))
            {
                PostStatusToBody(value.PaperId);
            }
        }

        private void OnTodoAction(PaperTodoActionInvocation invocation)
        {
            if (_disposed ||
                !string.Equals(invocation.ActionId, TodoActionId, StringComparison.Ordinal) &&
                !string.Equals(invocation.ActionId, TodoContextActionId, StringComparison.Ordinal))
            {
                return;
            }

            StartRequest($"todo:{invocation.Todo.Id}", () => RunTodoRequestAsync(invocation.Todo));
        }

        private void OnTopBarAction(PaperTopBarActionInvocation invocation)
        {
            if (_disposed ||
                !string.Equals(invocation.ActionId, TopBarActionId, StringComparison.Ordinal))
            {
                return;
            }

            StartRequest($"paper:{invocation.TargetPaperId}", () => RunPaperRequestAsync(invocation.TargetPaperId));
        }

        private void StartRequest(string key, Func<Task> run)
        {
            lock (_gate)
            {
                if (_disposed || !_inFlight.Add(key))
                {
                    return;
                }
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await run();
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[LlmBridge] Request failed: {ex}");
                }
                finally
                {
                    lock (_gate)
                    {
                        _inFlight.Remove(key);
                    }
                }
            });
        }

        private async Task RunTodoRequestAsync(TodoSnapshot todo)
        {
            var cancellationToken = _cancellation.Token;
            var settings = LlmBridgeSettings.Read(_context.Settings.Json);
            var paper = todo.LinkedPaperId is { Length: > 0 } linkedId
                ? _context.Workspace.GetPaper(linkedId)
                : null;
            var source = BuildTodoPromptSource(todo, paper);
            var prompt = LlmPromptBuilder.BuildTodoPrompt(source, _chinese);

            var configError = LlmChatRequest.ValidateSettings(settings, _chinese);
            if (configError != null)
            {
                WriteConfigProblemNote(configError);
                AppendLog("error", SourceLabel(todo.Text), configError);
                return;
            }

            string answer;
            try
            {
                answer = await LlmChatClient.SendChatAsync(settings, prompt, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                WriteTodoAnswer(
                    todo,
                    paper,
                    T("⚠️ 请求失败：", "⚠️ Request failed: ") + ex.Message,
                    settings.Model);
                AppendLog("error", SourceLabel(todo.Text), ex.Message);
                return;
            }

            WriteTodoAnswer(todo, paper, answer, settings.Model);
            AppendLog("ok", SourceLabel(todo.Text), T("回答已写入笔记。", "Answer written to a note."));
        }

        private async Task RunPaperRequestAsync(string paperId)
        {
            var cancellationToken = _cancellation.Token;
            var settings = LlmBridgeSettings.Read(_context.Settings.Json);
            var paper = _context.Workspace.GetPaper(paperId)
                        ?? throw new InvalidOperationException(
                            T("目标纸片已经不存在。", "The target paper no longer exists."));

            string prompt;
            try
            {
                prompt = BuildPaperPrompt(paper);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppendLog("error", SourceLabel(paper.Title), ex.Message);
                return;
            }

            var configError = LlmChatRequest.ValidateSettings(settings, _chinese);
            if (configError != null)
            {
                WriteConfigProblemNote(configError);
                AppendLog("error", SourceLabel(paper.Title), configError);
                return;
            }

            string answer;
            try
            {
                answer = await LlmChatClient.SendChatAsync(settings, prompt, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                CreateAnswerNote(
                    paper.Title,
                    T("⚠️ 请求失败：", "⚠️ Request failed: ") + ex.Message,
                    settings.Model);
                AppendLog("error", SourceLabel(paper.Title), ex.Message);
                return;
            }

            CreateAnswerNote(paper.Title, answer, settings.Model);
            AppendLog("ok", SourceLabel(paper.Title), T("回答已写入新笔记。", "Answer written to a new note."));
        }

        private LlmPromptSource BuildTodoPromptSource(TodoSnapshot todo, PaperSnapshot? linkedPaper)
        {
            string? linkedBody = null;
            var linkedBodyAvailable = false;
            if (linkedPaper != null)
            {
                if (string.Equals(linkedPaper.Type, "note", StringComparison.OrdinalIgnoreCase))
                {
                    var note = _context.Workspace.GetNote(linkedPaper.Id);
                    if (note?.ContentAvailable == true)
                    {
                        linkedBody = note.Content;
                        linkedBodyAvailable = true;
                    }
                }
                else if (string.Equals(linkedPaper.Type, "todo", StringComparison.OrdinalIgnoreCase))
                {
                    var builder = new System.Text.StringBuilder();
                    foreach (var item in _context.Workspace.ListTodos(linkedPaper.Id, includeBlank: false)
                                 .OrderBy(value => value.Order))
                    {
                        builder.Append(item.Done ? "- [x] " : "- [ ] ").AppendLine(item.Text);
                    }

                    linkedBody = builder.ToString();
                    linkedBodyAvailable = linkedBody.Length > 0;
                }
            }

            return new LlmPromptSource(
                todo.Text,
                todo.PaperTitle,
                todo.PaperId,
                todo.Id,
                todo.LinkedPath,
                linkedPaper?.Title,
                todo.LinkedPaperId,
                linkedBody,
                linkedBodyAvailable);
        }

        private string BuildPaperPrompt(PaperSnapshot paper)
        {
            var todoLines = new List<string>();
            string? noteBody = null;
            var noteBodyAvailable = false;
            if (string.Equals(paper.Type, "todo", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var todo in _context.Workspace.ListTodos(paper.Id, includeBlank: false)
                             .OrderBy(value => value.Order))
                {
                    var line = (todo.Done ? "- [x] " : "- [ ] ") + todo.Text;
                    if (!string.IsNullOrWhiteSpace(todo.LinkedPath))
                    {
                        line += T("  [绑定路径: ", "  [Linked path: ") + todo.LinkedPath + "]";
                    }
                    todoLines.Add(line);
                }
            }
            else if (string.Equals(paper.Type, "note", StringComparison.OrdinalIgnoreCase))
            {
                var note = _context.Workspace.GetNote(paper.Id);
                noteBody = note?.Content;
                noteBodyAvailable = note?.ContentAvailable == true;
            }

            return LlmPromptBuilder.BuildPaperPrompt(
                paper.Title,
                paper.Id,
                paper.Type,
                noteBody,
                noteBodyAvailable,
                todoLines,
                _chinese);
        }

        /// <summary>
        /// Writes the answer (or a failure marker) to the todo's destination note: appended to the
        /// linked note when there is one, otherwise written to a new note. The todo is linked to
        /// the new note only when it had no link before, or its old link pointed at a paper that
        /// has since been deleted.
        /// </summary>
        private void WriteTodoAnswer(
            TodoSnapshot todo,
            PaperSnapshot? linkedPaper,
            string answer,
            string model)
        {
            try
            {
                var timeStamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm");
                if (linkedPaper != null &&
                    string.Equals(linkedPaper.Type, "note", StringComparison.OrdinalIgnoreCase))
                {
                    _context.Workspace.WriteNote(new WriteNoteRequest
                    {
                        PaperId = linkedPaper.Id,
                        Content = LlmAnswerFormatter.FormatAnswerAppend(
                            todo.Text, model, answer, timeStamp, _chinese),
                        Mode = NoteWriteMode.Append
                    });
                    TryShowPaper(linkedPaper.Id);
                    return;
                }

                var created = _context.Workspace.CreatePaper(new CreatePaperRequest
                {
                    Type = "note",
                    Title = LlmAnswerFormatter.BuildAnswerNoteTitle(todo.Text, _chinese),
                    Content = LlmAnswerFormatter.FormatNewNoteContent(
                        todo.Text, model, answer, timeStamp, _chinese),
                    Show = true
                });
                var staleLink = todo.LinkedPaperId is { Length: > 0 } && linkedPaper == null;
                if (todo.LinkedPaperId is null or { Length: 0 } || staleLink)
                {
                    _context.Workspace.UpdateTodo(new UpdateTodoRequest
                    {
                        PaperId = todo.PaperId,
                        TodoId = todo.Id,
                        UpdateLinkedPaper = true,
                        LinkedPaperId = created.PaperId
                    });
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[LlmBridge] Failed to write the answer: {ex}");
            }
        }

        private void CreateAnswerNote(
            string paperTitle,
            string answer,
            string model)
        {
            try
            {
                var timeStamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm");
                _context.Workspace.CreatePaper(new CreatePaperRequest
                {
                    Type = "note",
                    Title = LlmAnswerFormatter.BuildAnswerNoteTitle(paperTitle, _chinese),
                    Content = LlmAnswerFormatter.FormatNewNoteContent(
                        paperTitle, model, answer, timeStamp, _chinese),
                    Show = true
                });
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[LlmBridge] Failed to create the answer note: {ex}");
            }
        }

        private void WriteConfigProblemNote(string problem)
        {
            try
            {
                var titleZh = "LLM Bridge 配置问题";
                var titleEn = "LLM Bridge configuration problem";
                var existing = _context.Workspace.ListPapers("note").FirstOrDefault(paper =>
                    string.Equals(paper.Title, titleZh, StringComparison.Ordinal) ||
                    string.Equals(paper.Title, titleEn, StringComparison.Ordinal));
                var stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm");
                if (existing != null)
                {
                    _context.Workspace.WriteNote(new WriteNoteRequest
                    {
                        PaperId = existing.Id,
                        Content = $"\n\n---\n\n**{stamp}**\n\n{problem}",
                        Mode = NoteWriteMode.Append
                    });
                    TryShowPaper(existing.Id);
                }
                else
                {
                    _context.Workspace.CreatePaper(new CreatePaperRequest
                    {
                        Type = "note",
                        Title = _chinese ? titleZh : titleEn,
                        Content = $"**{stamp}**\n\n{problem}",
                        Show = true
                    });
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[LlmBridge] Failed to report the configuration problem: {ex}");
            }
        }

        private void TryShowPaper(string paperId)
        {
            try
            {
                _context.WorkspacePresentation.ShowPaper(paperId);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[LlmBridge] Could not show the answer note: {ex}");
            }
        }

        private void AppendLog(string status, string source, string detail)
        {
            try
            {
                _state = _state.With(new LlmRequestLogEntry(
                    DateTimeOffset.Now.ToString("HH:mm:ss"),
                    source,
                    status,
                    detail));
                _context.State.Save(_state.Serialize());
                PublishStatusToBodies();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[LlmBridge] Failed to record the request log: {ex}");
            }
        }

        private void PublishStatusToBodies()
        {
            foreach (var paper in _context.Papers.List())
            {
                PostStatusToBody(paper.PaperId);
            }
        }

        private void PostStatusToBody(string paperId)
        {
            var lines = new List<string>();
            foreach (var entry in _state.Requests.TakeLast(5))
            {
                lines.Add($"{entry.Time}  [{entry.Status}]  {entry.Source} — {entry.Detail}");
            }

            try
            {
                _context.Papers.PostToBody(paperId, JsonSerializer.SerializeToElement(new
                {
                    type = "status",
                    lines = lines.ToArray()
                }));
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[LlmBridge] Could not publish status to the body: {ex}");
            }
        }

        private string SourceLabel(string text)
        {
            var label = (text ?? string.Empty).Trim();
            var newLine = label.IndexOfAny(['\r', '\n']);
            if (newLine >= 0)
            {
                label = label[..newLine].Trim();
            }

            if (label.Length > 40)
            {
                label = label[..40].TrimEnd() + "…";
            }

            return string.IsNullOrWhiteSpace(label) ? T("(无文本)", "(no text)") : label;
        }

        private string T(string zh, string en) => LlmBridgeText.For(_uiLanguage, zh, en);

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            // Cancel in-flight work without joining it: a worker blocked in a host call must not
            // make Dispose wait (see PaperPluginRuntimeContext contract notes).
            _cancellation.Cancel();
            _cancellation.Dispose();
            _runtimePaperSubscription.Dispose();
            _workspaceSubscription.Dispose();
            _context.TodoActions.SetActionHandler(null);
            _context.TodoActions.Clear();
            _context.GlobalTopBar.SetActionHandler(null);
            _context.GlobalTopBar.Clear();
        }
    }
}
