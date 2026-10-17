using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Input;
using Layeron.Plugin.Abstractions;
using Layeron.Scripting.Abstractions;
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

    private readonly IPluginServices _services;
    private readonly IScriptHostService? _host;
    private readonly AgentOptions _options;

    private IChatSession? _session;
    private CancellationTokenSource? _workCts;
    private bool _busy;
    private bool _disposed;
    private string _draft = "";
    private string _externalPath = "";
    private string _statusText = "就绪";

    public AgentChatViewModel(
        IPluginServices services,
        IScriptHostService? host,
        AgentOptions options)
    {
        _services = services;
        _host = host;
        _options = options;

        SendCommand = ReactiveCommand.CreateFromTask(SendAsync);
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
    /// Send is allowed with either text or attachments: a user who attaches a file and presses send has said
    /// something even without typing, and refusing that turn would be the panel inventing a rule.
    /// </summary>
    public bool CanSend => !_busy && (_draft.Length > 0 || Attachments.Count > 0);

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            this.RaiseAndSetIfChanged(ref _busy, value);
            this.RaisePropertyChanged(nameof(CanSend));
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

    private async Task SendAsync()
    {
        if (_busy)
            return;

        var text = _draft.Trim();
        var turn = new List<AgentAttachment>(Attachments);

        if (text.Length == 0 && turn.Count == 0)
            return;

        Append(new ChatMessage { Role = ChatRole.User, Text = text, Attachments = turn });
        Draft = "";

        if (!_options.KeepAttachments)
            Attachments.Clear();

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
    /// Drains one turn's deltas into the transcript.
    /// <para>
    /// The transcript is a log and a reply arrives in pieces, so a bubble has to be appended on the first
    /// piece and then grown in place - which is why <see cref="ChatMessage.Text"/> is a notifying property
    /// rather than an init-only one.
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
        var session = _session ??= new ChatSession(_options, _services, _host);

        var answer = new StringBuilder();
        var reasoning = new StringBuilder();
        ChatMessage? bubble = null;

        await foreach (var delta in session.StreamAsync(turn, ct).ConfigureAwait(true))
        {
            // Cancellation can arrive between two deltas, and the session cannot observe it until it asks
            // the provider for the next one.
            ct.ThrowIfCancellationRequested();

            var text = delta switch
            {
                ChatTextDelta t => t.Text,
                ChatReasoningDelta r => r.Text,
                _ => ""
            };

            if (text.Length == 0)
                continue;

            if (bubble is null)
            {
                // Appended on the first piece so the rest of the reply is visibly the same turn, and so a
                // cancellation mid-answer leaves what already arrived on screen.
                bubble = new ChatMessage { Role = ChatRole.Assistant };
                Append(bubble);
            }

            if (delta is ChatTextDelta)
            {
                answer.Append(text);
                bubble.Append(text);
            }
            else
            {
                reasoning.Append(text);
                bubble.AppendReasoning(text);
            }
        }

        return new TurnResult(answer.ToString(), reasoning.Length > 0);
    }

    private readonly record struct TurnResult(string Text, bool HadReasoning);

    private void Cancel()
    {
        if (!_busy)
            return;
        _workCts?.Cancel();
        StatusText = "正在取消…";
    }

    private void Clear()
    {
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

        DropSession();
        Attachments.Clear();
    }

    /// <inheritdoc />
    public void Dispose() => Detach();
}
