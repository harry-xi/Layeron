using System.Collections.ObjectModel;
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

    /// <summary>
    /// Caption for the part. Only the foldable kinds - reasoning and tool calls - carry one, because the
    /// caption is what a collapsed fold shows.
    /// </summary>
    public virtual string Header => "";

    /// <summary>The part's text, for the kinds that are nothing but text.</summary>
    public virtual string Body => _body;

    /// <summary>The colour this part's text is drawn in, chosen against the bubble rather than the theme.</summary>
    public virtual IBrush BodyBrush => ChatPalette.Answer;

    /// <summary>Extends the part's text, which is how a streamed reply is accumulated.</summary>
    public void Append(string text)
    {
        _body += text;
        this.RaisePropertyChanged(nameof(Body));
    }
}

/// <summary>The answer itself.</summary>
public sealed class ChatTextPart : ChatPart;

/// <summary>
/// The model's reasoning, where the provider exposes it. Its own part because it is not the answer - and
/// because dropping it is worse: a model that returns only reasoning would otherwise look like a failure.
/// </summary>
public sealed class ChatReasoningPart : ChatPart
{
    public override string Header => "思考";

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
    /// <summary>The calls in this run, in the order the model asked for them.</summary>
    public ObservableCollection<ChatToolCall> Calls { get; } = [];

    public override string Header => Calls.Count > 1 ? $"调用工具 ×{Calls.Count}" : "调用工具";

    /// <summary>State of the whole run: any call still running, any call that failed, or all done.</summary>
    public string StatusText => Calls.Any(call => !call.Completed)
        ? "调用中…"
        : Calls.Any(call => call.Failed)
            ? "有失败"
            : "已返回";

    public IBrush StatusBrush => Calls.Any(call => call.Failed) ? ChatPalette.Failed : ChatPalette.Muted;

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
/// One call inside a run: the request, its arguments, and the outcome once it arrives.
/// </summary>
public sealed class ChatToolCall : ReactiveObject
{
    private string _result = "";
    private bool _failed;
    private bool _completed;

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

    /// <summary>The row's state: a call still waiting for its result is a different thing to a finished one.</summary>
    public string StatusText => _completed ? (_failed ? "失败" : "已返回") : "调用中…";

    public IBrush StatusBrush => _failed ? ChatPalette.Failed : ChatPalette.Muted;

    /// <summary>Arguments are always shown, so a tool that takes none still reads as a call that was made.</summary>
    public string ArgumentsText => Arguments.Length > 0 ? Arguments : "（无参数）";

    /// <summary>The result line, or a caption for a call that returned nothing legible.</summary>
    public string ResultText => _result.Length > 0 ? _result : "（无输出）";

    /// <summary>Whether there is an outcome worth drawing yet.</summary>
    public bool HasResult => _completed;

    /// <summary>Result colour: a failed call must not read like a successful one.</summary>
    public IBrush ResultBrush => _failed ? ChatPalette.Failed : ChatPalette.Answer;

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
    public static readonly IBrush UserText = Brushes.White;
    public static readonly IBrush SystemText = new SolidColorBrush(Color.Parse("#909090"));
}
