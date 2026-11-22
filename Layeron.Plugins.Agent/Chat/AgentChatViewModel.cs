using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows.Input;
using Layeron.Plugin.Abstractions;
using Layeron.Plugins.Agent.Chat;
using Layeron.Scripting.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ReactiveUI;

namespace Layeron.Plugins.Agent;

/// <summary>
/// State and orchestration for the chat panel. The view declares layout and binds; everything that decides
/// what a message means lives here.
/// <para>
/// The panel reaches the application through <see cref="IPluginServices"/> for logging and, for file
/// dialogs, through the host's scripting seam. Nothing here references an Avalonia control.
/// </para>
/// <para>
/// The session is created on the first send and owned by this object. Which conversation is on screen is
/// shared with the history window through <see cref="AgentConversationManager"/>, but the provider session
/// itself is this panel's alone: restoring a transcript drops it and the next send seeds a new one from the
/// saved messages.
/// </para>
/// </summary>
public sealed class AgentChatViewModel : ReactiveObject, IDisposable
{
    /// <summary>
    /// How many turns the transcript keeps. A session can run for hours, and the transcript is a working
    /// area rather than an archive, so the oldest messages fall off instead of growing without bound.
    /// </summary>
    private const int TranscriptLimit = 500;

    /// <summary>
    /// The style key the panel's foldable parts - reasoning and tool calls - are tagged with.
    /// <para>
    /// Publishing it is what lets a test check that the folds the panel declares and the style that gives
    /// them a readable header have not drifted apart. The markup still has to spell the name literally:
    /// <c>Classes</c> takes a string, not a binding, so this constant cannot be referenced from XAML.
    /// </para>
    /// </summary>
    public const string FoldStyleKey = "chatFold";

    private readonly IPluginServices _services;
    private readonly IScriptHostService _host;
    private readonly AgentOptions _options;

    /// <summary>
    /// The conversation selected in the history window, shared with the chat panel. Its store lives in the
    /// workspace that was active when the plugin was activated, on purpose: switching workspaces changes
    /// where new work goes, not where chats that already belong to a workspace are kept.
    /// </summary>
    private readonly AgentConversationManager _conversations;

    /// <summary>Brings the independent history window forward; supplied by the plugin entry point.</summary>
    private readonly Action _openHistory;

    /// <summary>The handlers are kept so the subscriptions can be undone when the panel closes.</summary>
    private readonly Action _activeConversationChanged;
    private readonly Action<string, string> _conversationRenamed;

    private IChatSession? _session;
    private CancellationTokenSource? _workCts;
    private bool _busy;
    private bool _guiding;
    private bool _splitReply;
    private bool _disposed;
    private string _draft = "";
    private string _externalPath = "";
    private string _statusText = "就绪";
    private string? _conversationId;
    private string? _conversationTitle;
    private string _conversationTitleDraft = "新对话";
    private DateTimeOffset _conversationCreatedAt;

    /// <summary>
    /// The messages a newly created backend session is seeded with. It is set when a saved conversation is
    /// restored and cleared when a new one starts; the backend consumes it on its first turn, then owns the
    /// history itself.
    /// </summary>
    private IReadOnlyList<ChatMessage> _restoredHistory = [];

    /// <summary>
    /// Whether the transcript has changed since the last write. Opening a saved conversation or closing
    /// the panel without touching it must not make the conversation look recent: the history list orders
    /// by when the user actually spoke, not by when they looked.
    /// </summary>
    private bool _historyDirty;

    private readonly IPluginContext _context;

    public AgentChatViewModel(
        IPluginContext context,
        IPluginServices services,
        IScriptHostService host,
        AgentOptions options,
        AgentConversationManager conversations,
        Action openHistory)
    {
        _context = context;
        _services = services;
        _host = host;
        _options = options;
        _conversations = conversations;
        _openHistory = openHistory;

        // The history window can ask for a different conversation while this panel is open, so the panel
        // follows the shared selection rather than owning it.
        _activeConversationChanged = OnActiveConversationChanged;
        _conversationRenamed = OnConversationRenamed;
        _conversations.ActiveConversationChanged += _activeConversationChanged;
        _conversations.ConversationRenamed += _conversationRenamed;

        // Restored before the view binds, so the first paint is the conversation the user left behind
        // rather than an empty panel that fills in a moment later.
        LoadActiveConversation(announce: false);

        // Started rather than awaited: a turn can run for minutes, and a command that stayed executing
        // for its whole length would also keep the same button disabled when the user needs it to send
        // guidance into that turn.
        SendCommand = ReactiveCommand.Create(StartSend);
        CancelCommand = ReactiveCommand.Create(Cancel);
        NewConversationCommand = ReactiveCommand.Create(NewConversation);
        OpenHistoryCommand = ReactiveCommand.Create(_openHistory);
        BrowseAttachmentCommand = ReactiveCommand.CreateFromTask(BrowseAttachmentAsync);
        AddPathAttachmentCommand = ReactiveCommand.Create(AddPathAttachment);
        AddTextAttachmentCommand = ReactiveCommand.Create(AddTextAttachment);
        RemoveAllAttachmentsCommand = ReactiveCommand.Create(RemoveAllAttachments);
    }


    // -----------------------------------------------------------------------
    //  Transcript
    // -----------------------------------------------------------------------

    public ObservableCollection<ChatMessage> Messages { get; } = [];

    public bool HasMessages => Messages.Count > 0;

    /// <summary>
    /// The text in the toolbar's title box. It is edited in place and committed on focus loss or Enter;
    /// while a new conversation has no file yet, the edit is kept here until the first save gives it one.
    /// </summary>
    public string ConversationTitleDraft
    {
        get => _conversationTitleDraft;
        set => this.RaiseAndSetIfChanged(ref _conversationTitleDraft, value ?? "");
    }

    /// <summary>
    /// Saves an edited title. An empty box is not a name, so it falls back to the saved title and then to
    /// "新对话". Renaming a saved conversation goes through the manager so the history window updates too.
    /// </summary>
    public void CommitConversationTitle()
    {
        var title = _conversationTitleDraft.Trim();

        if (title.Length == 0)
        {
            SetConversationTitleDraft(_conversationTitle);
            return;
        }

        if (title == _conversationTitle)
        {
            SetConversationTitleDraft(title);
            return;
        }

        _conversationTitle = title;
        SetConversationTitleDraft(title);

        if (_conversationId is not null)
            _conversations.Rename(_conversationId, title);
    }

    /// <summary>Discards the edit, for Escape.</summary>
    public void RevertConversationTitle() => SetConversationTitleDraft(_conversationTitle);

    // -----------------------------------------------------------------------
    //  Input
    // -----------------------------------------------------------------------

    /// <summary>The text being composed, bound two-way to the input box.</summary>
    public string Draft
    {
        get => _draft;
        set
        {
            this.RaiseAndSetIfChanged(ref _draft, value ?? "");
            this.RaisePropertyChanged(nameof(CanSend));
        }
    }

    /// <summary>
    /// Path typed or dropped in by the user, used by the manual "路径" attachment. Separate from
    /// <see cref="Draft"/> on purpose: a message and the thing it refers to are different inputs, and
    /// overloading one box for both is what makes an attachment feel like a formatting trick.
    /// </summary>
    public string ExternalPath
    {
        get => _externalPath;
        set => this.RaiseAndSetIfChanged(ref _externalPath, value ?? "");
    }

    /// <summary>Attachments queued for the next message.</summary>
    public ObservableCollection<AgentAttachment> Attachments { get; } = [];

    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>
    /// Send is allowed whenever there is something to send: text or an attachment. While the agent is
    /// working the same button carries that content as guidance, so being busy is not a reason to disable
    /// it - the only disabled state is an empty input with nothing attached.
    /// </summary>
    public bool CanSend => _draft.Length > 0 || Attachments.Count > 0;

    /// <summary>The send button's caption: a click during a run is guidance, not a new message.</summary>
    public string SendLabel => _busy ? "指导" : "发送";

    /// <summary>The input box's placeholder, which says what pressing the button will do.</summary>
    public string InputHint => _busy
        ? "AI 正在工作；输入补充指导后按 Ctrl+Enter 发送"
        : "输入消息后按 Ctrl+Enter 发送";

    // -----------------------------------------------------------------------
    //  Options
    // -----------------------------------------------------------------------

    /// <summary>
    /// The panel's copy of the auto-approval preference. It is the same stored value the Agent settings
    /// page edits, exposed here because it changes how the next run behaves and is worth reaching without
    /// leaving the conversation.
    /// </summary>
    public bool AutoApproveTools
    {
        get => _options.AutoApproveTools;
        set
        {
            if (value == _options.AutoApproveTools)
                return;

            _options.Set(AgentOptions.Keys.AutoApproveTools, value);
            this.RaisePropertyChanged();
        }
    }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            this.RaiseAndSetIfChanged(ref _busy, value);
            this.RaisePropertyChanged(nameof(CanSend));
            this.RaisePropertyChanged(nameof(SendLabel));
            this.RaisePropertyChanged(nameof(InputHint));
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    public ICommand SendCommand { get; }

    public ICommand CancelCommand { get; }

    /// <summary>
    /// Starts a fresh conversation. It does not delete the current one: the current transcript is already
    /// saved, and the history window is the place to manage or remove it.
    /// </summary>
    public ICommand NewConversationCommand { get; }

    /// <summary>Brings the independent history window forward, where conversations can be restored or deleted.</summary>
    public ICommand OpenHistoryCommand { get; }

    /// <summary>Opens the host's file dialog and attaches the chosen file.</summary>
    public ICommand BrowseAttachmentCommand { get; }

    /// <summary>Attaches <see cref="ExternalPath"/> as a file attachment, without a dialog.</summary>
    public ICommand AddPathAttachmentCommand { get; }

    /// <summary>Attaches the current draft as a text attachment and clears the input box.</summary>
    public ICommand AddTextAttachmentCommand { get; }

    public ICommand RemoveAllAttachmentsCommand { get; }

    // -----------------------------------------------------------------------
    //  Sending
    // -----------------------------------------------------------------------

    /// <summary>
    /// Hands the turn to <see cref="SendAsync"/> and returns immediately. The task owns its exceptions,
    /// and the command must not stay in an executing state while the turn runs.
    /// </summary>
    private void StartSend() => _ = SendAsync();

    private async Task SendAsync()
    {
        // While the agent is working, the same button carries guidance into the running turn instead of
        // starting a second one. The panel already has the input, so this is the least surprising place
        // for "interrupt with a correction".
        if (_busy)
        {
            await GuideAsync().ConfigureAwait(true);
            return;
        }

        var text = _draft.Trim();
        var turn = new List<AgentAttachment>(Attachments);

        if (text.Length == 0 && turn.Count == 0)
            return;

        _splitReply = false;
        Append(new ChatMessage { Role = ChatRole.User, Body = text, Attachments = turn });
        Draft = "";

        // Saved before the provider is contacted: the user's message is already part of the transcript,
        // so a crash or an unplugged network cable must not take it with it.
        SaveHistory();

        if (!_options.KeepAttachments)
        {
            Attachments.Clear();
            RaiseAttachmentState();
        }

        var ct = ResetWork();

        IsBusy = true;
        StatusText = "思考中…";

        try
        {
            var reply = await PumpAsync(new ChatTurn { Text = text, Attachments = turn }, ct).ConfigureAwait(true);

            StatusText = reply.Text.Length > 0
                ? $"完成 · {DateTimeOffset.Now:HH:mm:ss}"
                : reply.HadReasoning
                    ? "只收到思考内容，没有回复文本"
                    : reply.ToolCalls > 0
                        ? $"完成 · 调用了 {reply.ToolCalls} 次工具，没有回复文本"
                        : "完成（回复为空）";
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消";
        }
        catch (Exception ex)
        {
            // A provider being down is not a reason to lose the transcript, and the reason belongs both on
            // screen and in the application log.
            _services.Log.Error("Agent 对话失败", ex, "Agent turn failed");
            Append(ChatMessage.Notice($"失败：{ex.Message}"));
            StatusText = "失败";
        }
        finally
        {
            IsBusy = false;

            // The whole turn - prose, reasoning, tool results and a failure notice, whichever happened -
            // is on screen by now, so this is the save that makes reopening the panel show it again.
            SaveHistory();
        }
    }

    /// <summary>
    /// Queues the draft as guidance for the turn that is already running. The transcript shows what the
    /// user said, while the agent receives it at its next service call; clearing the box only happens once
    /// the queue accepted it, so a failed send does not lose the text.
    /// </summary>
    private async Task GuideAsync()
    {
        // The button is still clickable while the turn runs, and a double click must not enqueue the
        // same draft twice before it is cleared.
        if (_guiding)
            return;

        var text = _draft.Trim();
        var turn = new List<AgentAttachment>(Attachments);

        if (text.Length == 0 && turn.Count == 0)
            return;

        if (_session is not { } session)
        {
            StatusText = "AI 尚未开始工作，无法发送指导";
            return;
        }

        _guiding = true;
        try
        {
            var token = _workCts?.Token ?? CancellationToken.None;
            await session.GuideAsync(new ChatTurn { Text = text, Attachments = turn }, token).ConfigureAwait(true);

            Draft = "";
            if (!_options.KeepAttachments)
            {
                Attachments.Clear();
                RaiseAttachmentState();
            }

            Append(new ChatMessage
            {
                Role = ChatRole.User,
                Body = text,
                Attachments = turn,
                IsGuidance = true
            });

            // The next assistant content belongs after the guidance, not above it: closing the current
            // reply is what keeps the transcript's order the order things were said in.
            _splitReply = true;

            StatusText = "已发送指导，AI 会在下一次模型调用时采纳";
            SaveHistory();
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消";
        }
        catch (Exception ex)
        {
            _services.Log.Error("发送指导失败", ex, "Agent guidance failed");
            Append(ChatMessage.Notice($"指导发送失败：{ex.Message}"));
            StatusText = "指导发送失败";
        }
        finally
        {
            _guiding = false;
        }
    }

    /// <summary>
    /// Drains one turn into the transcript, including the approval round-trips it needs.
    /// <para>
    /// The transcript is a log and a reply arrives in pieces, so a reply has to be appended on the first
    /// piece and then grown in place. A tool call is a piece like any other, and an approval request is a
    /// call that has stopped to ask: it becomes a row with a decision, and the run resumes only once every
    /// pending call has an answer - the framework binds all of them to the session and auto-rejects any
    /// request left unanswered.
    /// </para>
    /// <para>
    /// Reasoning is folded into the same reply as a named section. It is not the answer, but hiding it
    /// entirely is worse: when a model returns reasoning and nothing else, a panel that dropped it would
    /// show an empty reply and look broken.
    /// </para>
    /// </summary>
    private async Task<TurnResult> PumpAsync(ChatTurn turn, CancellationToken ct)
    {
        // Resolved once per conversation, not per turn: rebuilding the agent mid-conversation would take
        // the history with it. A turn that fails therefore keeps the session it failed on, and that
        // session is let go the next time the transcript is cleared.
        var session = _session ??= new ChatSession(
            _context,
            _options,
            _services,
            _host,
            _restoredHistory);

        var answer = new StringBuilder();
        var reasoning = new StringBuilder();
        ChatMessage? reply = null;
        var callOwners = new Dictionary<string, ChatMessage>();
        var toolCalls = 0;
        var pending = new List<PendingApproval>();

        // Guidance closes the reply that was on screen. The next piece of prose opens a new reply below
        // the guidance message, while a tool result still belongs to the reply that made the call.
        ChatMessage CurrentReply()
        {
            if (_splitReply)
            {
                _splitReply = false;
                reply = null;
            }

            return reply ??= AppendReply();
        }

        void Consume(AgentResponseUpdate update)
        {
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextReasoningContent { Text.Length: > 0 } reasoningChunk:
                        var reasoningReply = CurrentReply();
                        reasoning.Append(reasoningChunk.Text);
                        reasoningReply.AppendReasoning(reasoningChunk.Text);
                        break;

                    case TextContent { Text.Length: > 0 } text:
                        var textReply = CurrentReply();
                        answer.Append(text.Text);
                        textReply.AppendText(text.Text);
                        break;

                    // The call and its result are two events, not one, because they happen at different
                    // moments: a call that has no result yet is a call still running, which the panel can
                    // show and a single combined event could not. The id is the only thing that joins them.
                    case FunctionCallContent call:
                        var knownCall = callOwners.TryGetValue(call.CallId, out var existing);
                        var callReply = knownCall ? existing! : CurrentReply();
                        callOwners[call.CallId] = callReply;
                        if (!knownCall)
                            toolCalls++;
                        callReply.AddToolCall(call.CallId, call.Name, ChatFormat.Arguments(call.Arguments));
                        StatusText = $"调用工具 {call.Name}…";
                        break;

                    case FunctionResultContent result:
                        // A result belongs to the reply that made the call, wherever a guidance message
                        // has moved the current reply to.
                        var resultReply = callOwners.TryGetValue(result.CallId, out var owner)
                            ? owner
                            : CurrentReply();
                        resultReply.CompleteToolCall(
                            result.CallId,
                            ChatFormat.Result(result.Result),
                            result.Exception is not null);
                        StatusText = "思考中…";
                        break;

                    case ToolApprovalRequestContent approval:
                        RequestApproval(approval);
                        break;
                }
            }
        }

        void RequestApproval(ToolApprovalRequestContent approval)
        {
            var call = approval.ToolCall;
            var name = call is FunctionCallContent function ? function.Name : call.GetType().Name;
            var arguments = call is FunctionCallContent functionCall
                ? ChatFormat.Arguments(functionCall.Arguments)
                : "";

            // An approval for a call that is already on screen stays with that call's row, even if a
            // guidance message has since moved the reply on.
            var knownApproval = callOwners.TryGetValue(call.CallId, out var owner);
            var approvalReply = knownApproval ? owner! : CurrentReply();
            callOwners[call.CallId] = approvalReply;
            if (!knownApproval)
                toolCalls++;

            var decision = new PendingApproval(approval);
            pending.Add(decision);

            if (_options.AutoApproveTools)
            {
                // No question is asked, so no question row is built. The call row alone records what ran,
                // and resolving the approval the same way a click would keeps the transcript's state
                // honest without leaving a button nobody needs to press.
                decision.Call = approvalReply.AddToolCall(call.CallId, name, arguments);
                decision.Complete(approved: true);

                StatusText = $"自动通过：{name}";
                _services.Log.Info("工具 {Tool} 已自动通过，调用 {CallId}。", name, call.CallId);
                return;
            }

            decision.Call = approvalReply.AddToolApproval(call.CallId, name, arguments, decision.Complete);

            StatusText = $"等待确认：{name}";
            _services.Log.Info("工具 {Tool} 需要确认，调用 {CallId}。", name, call.CallId);
        }

        // Cancellation can arrive between two updates, and the session cannot observe it until it asks
        // the provider for the next one.
        await foreach (var update in session.StreamAsync(turn, ct).ConfigureAwait(true))
        {
            ct.ThrowIfCancellationRequested();
            Consume(update);
        }

        while (pending.Count > 0)
        {
            var responses = await AwaitApprovalsAsync(pending, ct).ConfigureAwait(true);

            pending.Clear();
            StatusText = "已确认，继续执行…";

            await foreach (var update in session.StreamApprovalsAsync(responses, ct).ConfigureAwait(true))
            {
                ct.ThrowIfCancellationRequested();
                Consume(update);
            }
        }

        return new TurnResult(answer.ToString(), reasoning.Length > 0, toolCalls);

        // Appended on the first piece so the rest of the reply is visibly the same turn, and so a
        // cancellation mid-answer leaves what already arrived on screen.
        ChatMessage AppendReply()
        {
            var message = new ChatMessage { Role = ChatRole.Assistant };
            Append(message);
            return message;
        }
    }

    /// <summary>
    /// Waits until every row in <paramref name="pending"/> has an answer, then turns the answers into the
    /// responses the framework expects. All requests from one run are answered together: sending one at a
    /// time would make the framework reject the ones still waiting.
    /// </summary>
    private static async Task<IReadOnlyList<ToolApprovalResponseContent>> AwaitApprovalsAsync(
        List<PendingApproval> pending,
        CancellationToken ct)
    {
        using var registration = ct.Register(() =>
        {
            foreach (var item in pending)
                item.Decision.TrySetCanceled(ct);
        });

        var decisions = await Task
            .WhenAll(pending.Select(item => item.Decision.Task))
            .ConfigureAwait(true);

        var responses = new List<ToolApprovalResponseContent>(pending.Count);
        for (var i = 0; i < pending.Count; i++)
            responses.Add(pending[i].Request.CreateResponse(decisions[i]));

        return responses;
    }

    /// <summary>
    /// One surfaced approval request and the row that asks for its answer. The task completes when the
    /// user decides, which is what releases the run after the stream has ended.
    /// </summary>
    private sealed class PendingApproval(ToolApprovalRequestContent request)
    {
        public ToolApprovalRequestContent Request { get; } = request;

        /// <summary>The transcript row, set once the row has been added to the transcript.</summary>
        public ChatToolCall? Call { get; set; }

        public TaskCompletionSource<bool> Decision { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete(bool approved)
        {
            Call?.ResolveApproval(approved);
            Decision.TrySetResult(approved);
        }
    }

    private readonly record struct TurnResult(string Text, bool HadReasoning, int ToolCalls);

    private void Cancel()
    {
        if (!_busy)
            return;
        _workCts?.Cancel();
        StatusText = "正在取消…";
    }

    /// <summary>
    /// Starts a new conversation. The transcript on screen is already saved, so this is a change of
    /// selection, not a deletion: the old chat stays in the history window and can be restored from there.
    /// </summary>
    private void NewConversation() => _conversations.StartNew();

    private void OnActiveConversationChanged() => LoadActiveConversation(announce: true);

    private void OnConversationRenamed(string id, string title)
    {
        if (_conversationId != id)
            return;

        _conversationTitle = title;
        SetConversationTitleDraft(title);
    }

    private void SetConversationTitleDraft(string? title)
    {
        _conversationTitleDraft = string.IsNullOrWhiteSpace(title) ? "新对话" : title;
        this.RaisePropertyChanged(nameof(ConversationTitleDraft));
    }

    private CancellationToken ResetWork()
    {
        _workCts?.Cancel();
        _workCts?.Dispose();
        _workCts = new CancellationTokenSource();
        return _workCts.Token;
    }

    // -----------------------------------------------------------------------
    //  Attachments
    // -----------------------------------------------------------------------

    private async Task BrowseAttachmentAsync()
    {
        if (_host is not { } host)
        {
            _services.Log.Warn("Agent 附件：宿主未提供文件对话框。");
            StatusText = "宿主不支持文件对话框，请用「路径」手动添加";
            return;
        }

        var path = await host.PickFileToOpenAsync("选择附件", host.WorkspaceRoot, ["*"]).ConfigureAwait(true);
        if (path is null)
            return;

        AddAttachment(AgentAttachment.FromFile(path));
    }

    private void AddPathAttachment()
    {
        var path = ExternalPath.Trim().Trim('"');
        if (path.Length == 0)
        {
            StatusText = "请先填写附件路径";
            return;
        }

        AddAttachment(AgentAttachment.FromFile(path));

        // The box is emptied on success so a second file does not need the first one deleted by hand.
        ExternalPath = "";
    }

    private void AddTextAttachment()
    {
        var text = Draft.Trim();
        if (text.Length == 0)
        {
            StatusText = "输入框为空，无法作为文本附件";
            return;
        }

        AddAttachment(AgentAttachment.FromText(text));
        Draft = "";
    }

    private void RemoveAllAttachments()
    {
        if (Attachments.Count == 0)
            return;

        Attachments.Clear();
        RaiseAttachmentState();
        StatusText = "已清空附件";
    }

    private void AddAttachment(AgentAttachment attachment)
    {
        // The chip binds its own remove command, so the list stays the only owner of its contents and an
        // attachment needs no reference back to this view model.
        attachment.RemoveCommand = ReactiveCommand.Create(() => RemoveAttachment(attachment));

        Attachments.Add(attachment);
        RaiseAttachmentState();
        StatusText = $"已添加附件：{attachment.Label}";
    }

    private void RemoveAttachment(AgentAttachment attachment)
    {
        if (!Attachments.Remove(attachment))
            return;

        RaiseAttachmentState();
    }

    private void RaiseAttachmentState()
    {
        this.RaisePropertyChanged(nameof(HasAttachments));
        this.RaisePropertyChanged(nameof(CanSend));
    }

    // -----------------------------------------------------------------------
    //  Transcript helpers
    // -----------------------------------------------------------------------

    private void Append(ChatMessage message)
    {
        Messages.Add(message);
        _historyDirty = true;

        while (Messages.Count > TranscriptLimit)
            Messages.RemoveAt(0);

        this.RaisePropertyChanged(nameof(HasMessages));
    }

    /// <summary>
    /// Shows the conversation the shared manager has selected. The transcript is a record, so a restored
    /// message is the same object the panel drew before, down to its folds and tool results; only a
    /// confirmation that was still waiting when the app closed is retired, because nothing could answer it.
    /// <para>
    /// Loading is also the one place that drops the provider's session: a restored transcript is a
    /// different conversation, and carrying the previous agent session into it would make the next reply
    /// answer a history that is no longer on screen.
    /// </para>
    /// </summary>
    private void LoadActiveConversation(bool announce)
    {
        // A turn may be waiting for a tool approval, which is not tied to the session's socket: cancel the
        // work so that wait is released instead of outliving the transcript it belonged to.
        _workCts?.Cancel();
        _splitReply = false;
        DropSession();

        Messages.Clear();
        _conversationId = null;
        _conversationTitle = null;
        SetConversationTitleDraft(null);
        _conversationCreatedAt = DateTimeOffset.Now;
        _historyDirty = false;

        var conversation = _conversations.LoadActive();

        // A restored transcript is also the context the next reply needs. The backend gets this list when
        // its session is created, so "打开历史对话" means "continue this one", not "show me an old picture".
        _restoredHistory = conversation?.Messages ?? [];

        if (conversation is not null)
        {
            _conversationId = conversation.Id;
            _conversationTitle = conversation.Title;
            SetConversationTitleDraft(conversation.Title);
            _conversationCreatedAt = conversation.CreatedAt;

            foreach (var message in conversation.Messages)
            {
                Messages.Add(message);

                while (Messages.Count > TranscriptLimit)
                    Messages.RemoveAt(0);
            }
        }

        this.RaisePropertyChanged(nameof(HasMessages));

        StatusText = conversation is null
            ? announce ? "新对话" : "就绪"
            : $"已恢复「{conversation.Title}」 · {Messages.Count} 条";
    }

    /// <summary>
    /// Persists the current conversation after a change. Every call is a complete snapshot, which is what
    /// makes a half-written turn recoverable: the next save replaces the file with whatever the panel now
    /// shows. A blank conversation is not written at all, so "新建对话" cannot litter the workspace with
    /// empty entries.
    /// </summary>
    private void SaveHistory()
    {
        if (Messages.Count == 0)
        {
            _historyDirty = false;
            return;
        }

        if (!_historyDirty)
            return;

        try
        {
            var now = DateTimeOffset.Now;

            _conversationId ??= Guid.NewGuid().ToString("N");

            if (string.IsNullOrWhiteSpace(_conversationTitle))
            {
                // A title typed before the first save is already a name the user chose. Otherwise the first
                // thing they said becomes the handle the history window shows.
                var draft = _conversationTitleDraft.Trim();
                _conversationTitle = draft.Length > 0 && draft != "新对话"
                    ? draft
                    : CreateTitle();
            }

            if (string.IsNullOrWhiteSpace(_conversationTitleDraft) || _conversationTitleDraft == "新对话")
                SetConversationTitleDraft(_conversationTitle);

            var conversation = new AgentConversation
            {
                Id = _conversationId,
                Title = _conversationTitle,
                CreatedAt = _conversationCreatedAt,
                UpdatedAt = now,
                Messages = [.. Messages]
            };

            _conversations.Save(conversation);
            _historyDirty = false;
        }
        catch (Exception ex)
        {
            // The store already treats a file problem as loggable, but a bug in the snapshot must not
            // turn into an exception on the UI thread either.
            _services.Log.Warn("Agent 聊天记录保存失败（{Message}）。", ex.Message);
        }
    }

    /// <summary>
    /// Names a conversation from the first thing the user actually said. A title is a handle for the
    /// history list, not an edit the user has to make before starting to type.
    /// </summary>
    private string CreateTitle()
    {
        var first = Messages.FirstOrDefault(message =>
            message.Role == ChatRole.User && !message.IsGuidance);

        var text = first?.Body.Trim();

        if (string.IsNullOrEmpty(text) && first is { Attachments.Count: > 0 })
            text = first.Attachments[0].TranscriptText;

        if (string.IsNullOrEmpty(text))
            return "新对话";

        text = text.ReplaceLineEndings(" ");

        // Text elements rather than UTF-16 code units: cutting an emoji in half would leave a lone
        // surrogate in the title, which is neither a readable title nor always valid JSON.
        var elements = new StringInfo(text);
        return elements.LengthInTextElements <= 30
            ? text
            : elements.SubstringByTextElements(0, 30) + "…";
    }

    // -----------------------------------------------------------------------
    //  Lifetime
    // -----------------------------------------------------------------------

    /// <summary>
    /// Lets go of the current conversation: the next turn starts a new one from the settings as they are
    /// then, which is also how a changed endpoint or model takes effect.
    /// <para>
    /// Disposal is fire-and-forget on purpose. It cancels a turn that may be in flight, and awaiting that
    /// from a synchronous path - a button handler, or host disposal - would block a UI thread on a network
    /// call. Nothing here needs the teardown to have finished.
    /// </para>
    /// </summary>
    private void DropSession()
    {
        var session = _session;
        _session = null;

        if (session is not null)
            _ = session.DisposeAsync();
    }

    /// <summary>
    /// Detaches from the host. Called when the panel closes or the plugin unloads, so nothing in this
    /// assembly stays reachable from a host object and the plugin's load context can be collected.
    /// </summary>
    public void Detach()
    {
        if (_disposed)
            return;
        _disposed = true;

        _conversations.ActiveConversationChanged -= _activeConversationChanged;
        _conversations.ConversationRenamed -= _conversationRenamed;

        _workCts?.Cancel();
        _workCts?.Dispose();
        _workCts = null;
        _splitReply = false;

        // The panel is going away, so this is the last chance to keep whatever arrived during a turn
        // that was still streaming when it closed.
        SaveHistory();

        DropSession();
        Attachments.Clear();
    }

    /// <inheritdoc />
    public void Dispose() => Detach();
}
