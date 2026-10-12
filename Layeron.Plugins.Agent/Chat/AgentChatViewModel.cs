using System.Collections.ObjectModel;
using System.Windows.Input;
using Layeron.Plugin.Abstractions;
using Layeron.Plugins.PythonRunner;
using Layeron.Scripting.Abstractions;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace Layeron.Plugins.Agent;

/// <summary>
/// State and orchestration for the chat panel. The view declares layout and binds; everything that decides
/// what a message means lives here.
/// <para>
/// The panel reaches the application through <see cref="IPluginServices"/> for logging and, for file
/// dialogs, through the host's scripting seam. Nothing here references an Avalonia control.
/// </para>
/// <para>
/// Not sealed, because the XAML designer needs a parameterless constructor and the only way to give it one
/// without adding a "design mode" branch to this class is to derive the design-time instance from it.
/// </para>
/// </summary>
public class AgentChatViewModel : ReactiveObject, IDisposable
{
    /// <summary>
    /// How many turns the transcript keeps. A session can run for hours, and the transcript is a working
    /// area rather than an archive, so the oldest messages fall off instead of growing without bound.
    /// </summary>
    private const int TranscriptLimit = 500;

    private readonly IPluginServices _services;
    private readonly IScriptHostService? _host;
    private readonly AgentOptions _options;

    private IChatSession _session = new EchoChatSession();
    private CancellationTokenSource? _workCts;
    private bool _busy;
    private bool _disposed;
    private string _draft = "";
    private string _externalPath = "";
    private string _statusText = "就绪";

    private readonly IPluginContext _context;

    public AgentChatViewModel(IPluginContext context,IPluginServices services, IScriptHostService? host, AgentOptions options)
    {
        _services = services;
        _host = host;
        _options = options;
        _context = context;

        SendCommand = ReactiveCommand.CreateFromTask(SendAsync);
        CancelCommand = ReactiveCommand.Create(Cancel);
        ClearCommand = ReactiveCommand.Create(Clear);
        BrowseAttachmentCommand = ReactiveCommand.CreateFromTask(BrowseAttachmentAsync);
        AddPathAttachmentCommand = ReactiveCommand.Create(AddPathAttachment);
        AddTextAttachmentCommand = ReactiveCommand.Create(AddTextAttachment);
        RemoveAllAttachmentsCommand = ReactiveCommand.Create(RemoveAllAttachments);
        CheckPythonCommand = ReactiveCommand.CreateFromTask(CheckPythonAsync);

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
    public ReactiveCommand<RxVoid, RxVoid> CheckPythonCommand { get; }

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
            var reply = await _session.SendAsync(text, turn, _options, ct).ConfigureAwait(true);

            if (reply.Length > 0)
                Append(new ChatMessage { Role = ChatRole.Assistant, Text = reply });

            StatusText = $"完成 · {DateTimeOffset.Now:HH:mm:ss}";
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
    /// Replaces the session that answers messages. The host-provided settings page only stores values, so
    /// whoever wires up a real provider calls this rather than the panel trying to build one from a URL.
    /// </summary>
    public void UseSession(IChatSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        StatusText = "已接入对话后端";
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

        Attachments.Clear();
    }

    private async Task CheckPythonAsync()
    {
        StatusText = "检查 Python…";
        try
        {
            var result = _context.Registry.GetPoint<IPythonRunnerService>(IPythonRunnerService.PointId);
            Console.WriteLine($"CheckPythonAsync: result={result}");
            StatusText = result != null ? "Python 可用" : "Python 不可用";
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消";
        }
        catch (Exception ex)
        {
            _services.Log.Error("Agent 检查 Python 失败", ex, "Agent check Python failed");
            StatusText = $"检查失败：{ex.Message}";
        }
    }


    /// <inheritdoc />
    public void Dispose() => Detach();
}
