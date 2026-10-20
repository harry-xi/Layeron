using System.Collections.ObjectModel;
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
/// The session is created on the first send and owned by this object: the panel is the only thing that
/// knows a conversation starts, ends or is thrown away, so it is the only thing that can hold the lifetime.
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
    private readonly IScriptHostService? _host;
    private readonly AgentOptions _options;

    private IChatSession? _session;
    private CancellationTokenSource? _workCts;
    private bool _busy;
    private bool _guiding;
    private bool _splitReply;
    private bool _disposed;
    private string _draft = "";
    private string _externalPath = "";
    private string _statusText = "就绪";

    private IPluginContext _context;

    public AgentChatViewModel(
        IPluginContext context,
        IPluginServices services,
        IScriptHostService? host,
        AgentOptions options)
    {
        _context = context;
        _services = services;
        _host = host;
        _options = options;

        // Started rather than awaited: a turn can run for minutes, and a command that stayed executing
        // for its whole length would also keep the same button disabled when the user needs it to send
        // guidance into that turn.
        SendCommand = ReactiveCommand.Create(StartSend);
        CancelCommand = ReactiveCommand.Create(Cancel);
        ClearCommand = ReactiveCommand.Create(Clear);
        BrowseAttachmentCommand = ReactiveCommand.CreateFromTask(BrowseAttachmentAsync);
        AddPathAttachmentCommand = ReactiveCommand.Create(AddPathAttachment);
        AddTextAttachmentCommand = ReactiveCommand.Create(AddTextAttachment);
        RemoveAllAttachmentsCommand = ReactiveCommand.Create(RemoveAllAttachments);

        Append(ChatMessage.Notice(_host is null
            ? "宿主未提供文件对话框，可用「路径」或「文本」手动添加附件。"
            : "就绪。输入消息后发送，或先添加附件。"));
    }


    // -----------------------------------------------------------------------
    //  Transcript
    // -----------------------------------------------------------------------

    public ObservableCollection<ChatMessage> Messages { get; } = [];

    public bool HasMessages => Messages.Count > 0;

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
        ? "AI 正在工作；输入补充指导后点「指导」"
        : "输入消息后点发送";

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

    public ICommand ClearCommand { get; }

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
            // bubble is what keeps the transcript's order the order things were said in.
            _splitReply = true;

            StatusText = "已发送指导，AI 会在下一次模型调用时采纳";
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
    /// The transcript is a log and a reply arrives in pieces, so a bubble has to be appended on the first
    /// piece and then grown in place. A tool call is a piece like any other, and an approval request is a
    /// call that has stopped to ask: it becomes a row with a decision, and the run resumes only once every
    /// pending call has an answer - the framework binds all of them to the session and auto-rejects any
    /// request left unanswered.
    /// </para>
    /// <para>
    /// Reasoning is folded into the same bubble as a named section. It is not the answer, but hiding it
    /// entirely is worse: when a model returns reasoning and nothing else, a panel that dropped it would
    /// show an empty reply and look broken.
    /// </para>
    /// </summary>
    private async Task<TurnResult> PumpAsync(ChatTurn turn, CancellationToken ct)
    {
        // Resolved once per conversation, not per turn: rebuilding the agent mid-conversation would take
        // the history with it. A turn that fails therefore keeps the session it failed on, and that
        // session is let go the next time the transcript is cleared.
        var session = _session ??= new ChatSession(_context, _options, _services, _host);

        var answer = new StringBuilder();
        var reasoning = new StringBuilder();
        ChatMessage? bubble = null;
        var callOwners = new Dictionary<string, ChatMessage>();
        var toolCalls = 0;
        var pending = new List<PendingApproval>();

        // Guidance closes the reply that was on screen. The next piece of prose opens a new bubble below
        // the guidance message, while a tool result still belongs to the bubble that made the call.
        ChatMessage CurrentBubble()
        {
            if (_splitReply)
            {
                _splitReply = false;
                bubble = null;
            }

            return bubble ??= AppendBubble();
        }

        void Consume(AgentResponseUpdate update)
        {
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextReasoningContent { Text.Length: > 0 } reasoningChunk:
                        var reasoningBubble = CurrentBubble();
                        reasoning.Append(reasoningChunk.Text);
                        reasoningBubble.AppendReasoning(reasoningChunk.Text);
                        break;

                    case TextContent { Text.Length: > 0 } text:
                        var textBubble = CurrentBubble();
                        answer.Append(text.Text);
                        textBubble.AppendText(text.Text);
                        break;

                    // The call and its result are two events, not one, because they happen at different
                    // moments: a call that has no result yet is a call still running, which the panel can
                    // show and a single combined event could not. The id is the only thing that joins them.
                    case FunctionCallContent call:
                        var knownCall = callOwners.TryGetValue(call.CallId, out var existing);
                        var callBubble = knownCall ? existing! : CurrentBubble();
                        callOwners[call.CallId] = callBubble;
                        if (!knownCall)
                            toolCalls++;
                        callBubble.AddToolCall(call.CallId, call.Name, ChatFormat.Arguments(call.Arguments));
                        StatusText = $"调用工具 {call.Name}…";
                        break;

                    case FunctionResultContent result:
                        // A result belongs to the bubble that made the call, wherever a guidance message
                        // has moved the current bubble to.
                        var resultBubble = callOwners.TryGetValue(result.CallId, out var owner)
                            ? owner
                            : CurrentBubble();
                        resultBubble.CompleteToolCall(
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
            var approvalBubble = knownApproval ? owner! : CurrentBubble();
            callOwners[call.CallId] = approvalBubble;
            if (!knownApproval)
                toolCalls++;

            var decision = new PendingApproval(approval);
            pending.Add(decision);

            decision.Call = approvalBubble.AddToolApproval(call.CallId, name, arguments, decision.Complete);

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
        ChatMessage AppendBubble()
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

        /// <summary>The transcript row, set once the row has been added to the bubble.</summary>
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

    private void Clear()
    {
        // A turn may be waiting for a tool approval, which is not tied to the session's socket: cancel the
        // work so that wait is released instead of outliving the transcript it belonged to.
        _workCts?.Cancel();
        _splitReply = false;

        Messages.Clear();
        this.RaisePropertyChanged(nameof(HasMessages));

        // The transcript is the conversation the user sees, so throwing it away has to throw away the
        // provider's copy too; otherwise the next reply answers a history that is no longer on screen.
        DropSession();

        StatusText = "已清空";
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

        while (Messages.Count > TranscriptLimit)
            Messages.RemoveAt(0);

        this.RaisePropertyChanged(nameof(HasMessages));
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

        _workCts?.Cancel();
        _workCts?.Dispose();
        _workCts = null;
        _splitReply = false;

        DropSession();
        Attachments.Clear();
    }

    /// <inheritdoc />
    public void Dispose() => Detach();
}
