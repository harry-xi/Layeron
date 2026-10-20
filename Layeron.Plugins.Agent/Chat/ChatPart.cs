using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Media;
using ReactiveUI;

namespace Layeron.Plugins.Agent;

/// <summary>
/// One piece of a reply, in the order it was produced.
/// <para>
/// A reply is not "some reasoning, then an answer": the model thinks, calls a tool, answers, then thinks
/// again. The panel has to render that order, so parts are appended to a list as they arrive rather than
/// gathered into one field per kind - a field per kind could only ever regroup the reply.
/// </para>
/// <para>
/// Every brush travels with the part instead of being read from the ambient theme. The bubble is drawn on a
/// dark background the app theme knows nothing about, so a part that fell back to the theme's default
/// foreground would be black text on a dark bubble.
/// </para>
/// <para>
/// A part that grows - streamed prose, reasoning - notifies instead of being replaced, so its view row keeps
/// its place in the list and only its text moves.
/// </para>
/// </summary>
public abstract class ChatPart : ReactiveObject
{
    private string _body = "";
    private bool _isExpanded;

    /// <summary>
    /// Caption for the part. Only the foldable kinds - reasoning and tool calls - carry one, because the
    /// caption is what a collapsed fold shows.
    /// <para>
    /// A caption may depend on <see cref="IsExpanded"/>: what it summarises is visible once the fold is open,
    /// and a summary of something already on screen is just noise.
    /// </para>
    /// </summary>
    public virtual string Header => "";

    /// <summary>
    /// Whether the fold is open, and therefore whether its content is on screen.
    /// <para>
    /// The state lives here rather than in the control because the caption is derived from it: a fold that
    /// drew its caption itself would still be describing something the user can already read.
    /// </para>
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            this.RaiseAndSetIfChanged(ref _isExpanded, value);
            this.RaisePropertyChanged(nameof(Header));
        }
    }

    /// <summary>The part's text, for the kinds that are nothing but text.</summary>
    public virtual string Body => _body;

    /// <summary>The colour this part's text is drawn in, chosen against the bubble rather than the theme.</summary>
    public virtual IBrush BodyBrush => ChatPalette.Answer;

    /// <summary>Extends the part's text, which is how a streamed reply is accumulated.</summary>
    public void Append(string text)
    {
        _body += text;
        this.RaisePropertyChanged(nameof(Body));

        // The caption of a part that summarises its own text has to move with it, or a collapsed fold would
        // keep describing the first few characters it ever saw.
        this.RaisePropertyChanged(nameof(Header));
    }

    /// <summary>
    /// The first few characters of a piece of text, for use as a caption. Whitespace is collapsed first so a
    /// fold caption stays one short line, and the ellipsis is only added when something was really cut.
    /// </summary>
    protected static string Summarize(string text, int length = 5)
    {
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return flat.Length <= length ? flat : flat[..length] + "…";
    }
}

/// <summary>The answer itself.</summary>
public sealed class ChatTextPart : ChatPart;

/// <summary>
/// The model's reasoning, where the provider exposes it. Its own part because it is not the answer - and
/// because dropping it is worse: a model that returns only reasoning would otherwise look like a failure.
/// <para>
/// The fold is collapsed by default and reasoning is long, so the caption carries the start of it: enough to
/// tell two trains of thought apart without opening either.
/// </para>
/// </summary>
public sealed class ChatReasoningPart : ChatPart
{
    /// <summary>
    /// Just the label while the fold is open: the reasoning itself is on screen, so summarising it would be
    /// repeating what the user is looking at.
    /// </summary>
    public override string Header => Body.Length == 0 || IsExpanded
        ? "思考"
        : $"思考 · {Summarize(Body)}";

    public override IBrush BodyBrush => ChatPalette.Muted;
}

/// <summary>
/// One run of tool calls: what the model asked for, and what each call returned.
/// <para>
/// Consecutive calls share one part because that is how they are produced: a model can ask for several tools
/// in a single step and their results come back together, so one caption over the calls is what actually
/// happened, while a caption per call would invent a series of separate decisions.
/// </para>
/// <para>
/// A call is added when it is asked for and completed when its result arrives, both into the same row - the
/// request and its outcome are one fact, and a call with no result yet is a call still running.
/// </para>
/// <para>
/// The run's own state is on the caption rather than only on the rows, because the panel shows this collapsed
/// by default: a fold whose caption said nothing would make the user open it to find out whether anything
/// had happened at all.
/// </para>
/// </summary>
public sealed class ChatToolCallPart : ChatPart
{
    /// <summary>
    /// The calls in this run, in the order the model asked for them.
    /// </summary>
    public ObservableCollection<ChatToolCall> Calls { get; } = [];

    /// <summary>
    /// The caption, which names the tools: a collapsed fold that only said "调用工具" would leave the user
    /// opening it to find out what was called. Several calls are named together up to the point where the
    /// caption stops being readable, then counted instead.
    /// <para>
    /// While the fold is open the names are dropped: every call below is already headed by its own tool name.
    /// </para>
    /// </summary>
    public override string Header
    {
        get
        {
            if (Calls.Count == 0 || IsExpanded)
                return "调用工具";

            var names = string.Join(" + ", Calls.Select(call => call.Name));

            return Calls.Count > NamedCallLimit ? $"调用工具 ×{Calls.Count}" : $"调用工具 · {names}";
        }
    }

    /// <summary>How many tool names a caption holds before it says how many there were instead.</summary>
    private const int NamedCallLimit = 3;

    /// <summary>
    /// State of the whole run: a call waiting on the user is not the same as a call waiting on a tool, and
    /// both must be visible while the run is collapsed.
    /// </summary>
    public string StatusText => Calls.Any(call => call.Approval == ChatToolApprovalState.Pending)
        ? "等待确认"
        : Calls.Any(call => call.Approval == ChatToolApprovalState.Rejected)
            ? "已拒绝"
            : Calls.Any(call => call.Failed)
                ? "有失败"
                : Calls.Any(call => !call.Completed)
                    ? "调用中…"
                    : "已返回";

    public IBrush StatusBrush => Calls.Any(call => call.Approval == ChatToolApprovalState.Rejected || call.Failed)
        ? ChatPalette.Failed
        : Calls.Any(call => call.Approval == ChatToolApprovalState.Pending)
            ? ChatPalette.Answer
            : ChatPalette.Muted;

    /// <summary>Adds a call, or returns the existing row when the same call is reported again.</summary>
    public ChatToolCall Add(string callId, string name, string arguments)
    {
        if (Find(callId) is { } existing)
            return existing;

        var call = new ChatToolCall { CallId = callId, Name = name, Arguments = arguments };

        // The caption's state is derived from the calls, so it has to move when any of them does.
        call.PropertyChanged += (_, _) =>
        {
            this.RaisePropertyChanged(nameof(StatusText));
            this.RaisePropertyChanged(nameof(StatusBrush));
        };

        Calls.Add(call);

        this.RaisePropertyChanged(nameof(Header));
        this.RaisePropertyChanged(nameof(StatusText));
        this.RaisePropertyChanged(nameof(StatusBrush));

        return call;
    }

    public ChatToolCall? Find(string callId) => Calls.FirstOrDefault(call => call.CallId == callId);
}

/// <summary>
/// Where a call stands with respect to human approval.
/// <para>
/// Most calls never ask: <see cref="NotRequired"/> is the ordinary state and keeps the row's status about
/// the tool, not about the user. The other states exist because a call waiting on a person is a different
/// thing from a call waiting on a tool, and a rejected call is not a call that failed on its own.
/// </para>
/// </summary>
public enum ChatToolApprovalState
{
    /// <summary>The tool does not need a confirmation.</summary>
    NotRequired,

    /// <summary>A confirmation was requested and the user has not answered yet.</summary>
    Pending,

    /// <summary>The user allowed the call.</summary>
    Approved,

    /// <summary>The user rejected the call; it will not run.</summary>
    Rejected
}

/// <summary>
/// One call inside a run: the request, its arguments, and the outcome once it arrives.
/// <para>
/// A call can also be waiting on the user rather than on the tool. That state lives here because the row
/// that shows the arguments is the row that has to offer the decision.
/// </para>
/// </summary>
public sealed class ChatToolCall : ReactiveObject
{
    private string _result = "";
    private bool _failed;
    private bool _completed;
    private ChatToolApprovalState _approval;

    public required string CallId { get; init; }

    /// <summary>The tool's name as the model asked for it.</summary>
    public required string Name { get; init; }

    /// <summary>The arguments rendered to text. Empty when the tool takes none.</summary>
    public string Arguments { get; init; } = "";

    /// <summary>What the call returned; empty until it arrives.</summary>
    public string Result
    {
        get => _result;
        private set => this.RaiseAndSetIfChanged(ref _result, value);
    }

    /// <summary>True when the call raised instead of returning.</summary>
    public bool Failed
    {
        get => _failed;
        private set => this.RaiseAndSetIfChanged(ref _failed, value);
    }

    /// <summary>Whether a result has arrived yet.</summary>
    public bool Completed
    {
        get => _completed;
        private set => this.RaiseAndSetIfChanged(ref _completed, value);
    }

    /// <summary>Whether this call needs a decision, is waiting for one, or has already had one.</summary>
    public ChatToolApprovalState Approval
    {
        get => _approval;
        private set
        {
            if (_approval == value)
                return;

            _approval = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(NeedsApproval));
            this.RaisePropertyChanged(nameof(StatusText));
            this.RaisePropertyChanged(nameof(StatusBrush));
        }
    }

    /// <summary>Whether the row should offer approve and reject buttons.</summary>
    public bool NeedsApproval => _approval == ChatToolApprovalState.Pending;

    /// <summary>Allows the call. Only meaningful while <see cref="NeedsApproval"/> is true.</summary>
    public ICommand? ApproveCommand { get; private set; }

    /// <summary>Rejects the call. Only meaningful while <see cref="NeedsApproval"/> is true.</summary>
    public ICommand? RejectCommand { get; private set; }

    /// <summary>
    /// The row's state: approval comes first, because a call waiting on the user must not say it is running
    /// as if the tool had already been asked.
    /// </summary>
    public string StatusText => _approval switch
    {
        ChatToolApprovalState.Pending => "等待确认",
        ChatToolApprovalState.Rejected => "已拒绝",
        ChatToolApprovalState.Approved when !_completed => "已批准",
        _ => _completed ? (_failed ? "失败" : "已返回") : "调用中…"
    };

    public IBrush StatusBrush => _approval is ChatToolApprovalState.Pending
        ? ChatPalette.Answer
        : _approval == ChatToolApprovalState.Rejected || _failed
            ? ChatPalette.Failed
            : ChatPalette.Muted;

    /// <summary>Arguments are always shown, so a tool that takes none still reads as a call that was made.</summary>
    public string ArgumentsText => Arguments.Length > 0 ? Arguments : "（无参数）";

    /// <summary>The result line, or a caption for a call that returned nothing legible.</summary>
    public string ResultText => _result.Length > 0 ? _result : "（无输出）";

    /// <summary>Whether there is an outcome worth drawing yet.</summary>
    public bool HasResult => _completed;

    /// <summary>Result colour: a failed call must not read like a successful one.</summary>
    public IBrush ResultBrush => _failed ? ChatPalette.Failed : ChatPalette.Answer;

    /// <summary>
    /// Turns the row into a question and wires the two answers. The commands are created here rather than
    /// in the panel so the row owns the only state they change; <paramref name="decide"/> is how the
    /// conversation that asked learns the answer.
    /// </summary>
    public void RequireApproval(Action<bool> decide)
    {
        ApproveCommand = ReactiveCommand.Create(() =>
        {
            ResolveApproval(approved: true);
            decide(true);
        });
        RejectCommand = ReactiveCommand.Create(() =>
        {
            ResolveApproval(approved: false);
            decide(false);
        });

        Approval = ChatToolApprovalState.Pending;

        // The row may already have been added to the transcript before the approval arrived, so the
        // commands are announced rather than assumed to be seen at construction time.
        this.RaisePropertyChanged(nameof(ApproveCommand));
        this.RaisePropertyChanged(nameof(RejectCommand));
    }

    /// <summary>Records the user's answer. A rejection is final; an approval still waits for the tool.</summary>
    public void ResolveApproval(bool approved) =>
        Approval = approved ? ChatToolApprovalState.Approved : ChatToolApprovalState.Rejected;

    /// <summary>Records the outcome, which is what turns the row from in-flight into finished.</summary>
    public void Complete(string result, bool failed)
    {
        Result = result;
        Failed = failed;
        Completed = true;

        // The caption and the colours are derived from the values above, so they change with the completion
        // even when the values being set did not themselves move.
        this.RaisePropertyChanged(nameof(StatusText));
        this.RaisePropertyChanged(nameof(StatusBrush));
        this.RaisePropertyChanged(nameof(ResultText));
        this.RaisePropertyChanged(nameof(ResultBrush));
        this.RaisePropertyChanged(nameof(HasResult));
    }
}

/// <summary>
/// The colours the transcript draws with, declared once because the transcript is a dark surface inside a
/// themed window and cannot inherit the theme's own foregrounds.
/// </summary>
internal static class ChatPalette
{
    public static readonly IBrush Bubble = new SolidColorBrush(Color.Parse("#2B2B2B"));

    /// <summary>The answer, and anything read as content: the one colour that must stay legible on the bubble.</summary>
    public static readonly IBrush Answer = new SolidColorBrush(Color.Parse("#E8E8E8"));

    /// <summary>Reasoning, captions and argument text: present, but plainly not the answer.</summary>
    public static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#A8A8A8"));

    public static readonly IBrush Failed = new SolidColorBrush(Color.Parse("#E08A8A"));
    public static readonly IBrush UserBubble = new SolidColorBrush(Color.Parse("#2D6CDF"));

    /// <summary>Guidance is still the user speaking, but mid-turn: a quieter blue says so.</summary>
    public static readonly IBrush GuidanceBubble = new SolidColorBrush(Color.Parse("#2A4E7A"));

    public static readonly IBrush UserText = Brushes.White;
    public static readonly IBrush SystemText = new SolidColorBrush(Color.Parse("#909090"));
}
