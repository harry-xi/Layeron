using System.Collections.ObjectModel;
using ReactiveUI;

namespace Layeron.Plugins.Agent;

/// <summary>Who produced a message.</summary>
public enum ChatRole
{
    /// <summary>The user.</summary>
    User,

    /// <summary>The agent.</summary>
    Assistant,

    /// <summary>Host-side notice, e.g. "the volume changed".</summary>
    System
}

/// <summary>
/// One message in the conversation.
/// <para>
/// A user or system message is fixed once appended: the transcript is an append-only log, so a message that
/// could be edited later would make "what did the agent actually see" unanswerable. An assistant message is
/// the exception, because a reply arrives as a stream - it is grown in place, which is the difference between
/// watching the model work and watching a spinner.
/// </para>
/// <para>
/// An assistant reply is a sequence of <see cref="ChatPart"/>s rather than a body plus a few side properties.
/// The model thinks, calls a tool, answers, then thinks again; a message that kept reasoning, calls and prose
/// apart could only ever render that reply as three things that never interleaved.
/// </para>
/// <para>
/// Role flags and labels are view facts rather than agent facts, and they live here instead of in a converter
/// because the transcript header is the only thing that varies by role; the colours it draws with come from
/// the application theme.
/// </para>
/// </summary>
public sealed class ChatMessage : ReactiveObject
{
    /// <summary>
    /// A plain body, used by user and system messages. An assistant reply leaves this empty and fills
    /// <see cref="Parts"/> instead, because its content has kinds and an order.
    /// </summary>
    public string Body { get; init; } = "";

    public required ChatRole Role { get; init; }

    /// <summary>
    /// The reply as it was produced, in order.
    /// <para>
    /// Consecutive pieces of the same kind extend the part already at the end rather than adding another one,
    /// which is what keeps streamed prose as one paragraph and a burst of tool calls as one run. The list is
    /// only ever appended to, so its order is the reply's own order.
    /// </para>
    /// </summary>
    public ObservableCollection<ChatPart> Parts { get; } = [];

    public bool HasBody => Body.Length > 0;

    public bool HasParts => Parts.Count > 0;

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    /// <summary>
    /// What rode along with this message. The same <see cref="AgentAttachment"/> instances the input area
    /// produced are kept, so the payload a backend receives is the object the user attached rather than a
    /// label re-parsed out of text.
    /// </summary>
    public IReadOnlyList<AgentAttachment> Attachments { get; init; } = [];

    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>
    /// True for a message the user sent into a turn that was already running. It is still the user's
    /// voice, but it is not a new turn, and the transcript has to say which one it was.
    /// </summary>
    public bool IsGuidance { get; init; }

    /// <summary>Caption above the body, e.g. "你 · 14:03".</summary>
    public string Header => Role switch
    {
        ChatRole.User when IsGuidance => $"你（指导） · {Timestamp:HH:mm}",
        ChatRole.User => $"你 · {Timestamp:HH:mm}",
        ChatRole.Assistant => $"Agent · {Timestamp:HH:mm}",
        _ => Timestamp.ToString("HH:mm")
    };

    /// <summary>
    /// Role as it reads in the transcript header. Guidance is still the user speaking; the view marks it
    /// with a small badge next to this label instead of inventing a third role.
    /// </summary>
    public string RoleLabel => Role switch
    {
        ChatRole.User => "你",
        ChatRole.Assistant => "Agent",
        _ => "系统"
    };

    /// <summary>Local wall-clock time, the smallest thing that separates two messages from the same minute.</summary>
    public string TimeText => Timestamp.ToString("HH:mm");

    /// <summary>Role flags for the view's conditional classes; colours themselves stay in the theme.</summary>
    public bool IsUser => Role == ChatRole.User;

    public bool IsAssistant => Role == ChatRole.Assistant;

    public bool IsSystem => Role == ChatRole.System;

    /// <summary>
    /// Appends streamed answer text. Text that continues the previous text extends that part, so a reply that
    /// streams piece by piece stays one paragraph instead of becoming one part per piece.
    /// </summary>
    public void AppendText(string text) => AppendPart<ChatTextPart>(text);

    /// <summary>Appends streamed reasoning, on the same terms as <see cref="AppendText"/>.</summary>
    public void AppendReasoning(string text) => AppendPart<ChatReasoningPart>(text);

    /// <summary>
    /// Records that the reply asked for a tool. Calls that arrive together share one part, which is how the
    /// model produced them: asking for several tools in one step is one step.
    /// </summary>
    public ChatToolCall AddToolCall(string callId, string name, string arguments) =>
        EnsureToolCallPart().Add(callId, name, arguments);

    /// <summary>
    /// Records a call that is waiting for the user, and opens the fold that holds it: a confirmation the
    /// user cannot see is the same as no confirmation at all.
    /// </summary>
    public ChatToolCall AddToolApproval(
        string callId,
        string name,
        string arguments,
        Action<bool> decide)
    {
        var part = EnsureToolCallPart();
        var call = part.Add(callId, name, arguments);

        call.RequireApproval(decide);
        part.IsExpanded = true;

        return call;
    }

    private ChatToolCallPart EnsureToolCallPart()
    {
        if (Parts.Count > 0 && Parts[^1] is ChatToolCallPart trailing)
            return trailing;

        var part = new ChatToolCallPart();
        Parts.Add(part);
        this.RaisePropertyChanged(nameof(HasParts));

        return part;
    }

    /// <summary>
    /// Attaches an outcome to the call it belongs to. A result whose call this message never recorded is
    /// dropped rather than invented as a row: a row with no request in it would say the opposite of what
    /// happened.
    /// </summary>
    public void CompleteToolCall(string callId, string result, bool failed) =>
        Parts
            .OfType<ChatToolCallPart>()
            .Select(part => part.Find(callId))
            .FirstOrDefault(call => call is not null)
            ?.Complete(result, failed);

    private void AppendPart<TPart>(string text) where TPart : ChatPart, new()
    {
        if (Parts.Count > 0 && Parts[^1] is TPart trailing)
        {
            trailing.Append(text);
            return;
        }

        var part = new TPart();
        part.Append(text);
        Parts.Add(part);

        this.RaisePropertyChanged(nameof(HasParts));
    }

    /// <summary>Creates a system notice.</summary>
    public static ChatMessage Notice(string text) => new() { Role = ChatRole.System, Body = text };
}
