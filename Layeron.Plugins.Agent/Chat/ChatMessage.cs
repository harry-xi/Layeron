using System.Collections.ObjectModel;
using Avalonia.Layout;
using Avalonia.Media;
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
/// Alignment and brushes are view facts rather than agent facts, and they live here instead of in a converter
/// because the bubble is the only thing that varies by role.
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

    /// <summary>Caption above the body, e.g. "你 · 14:03".</summary>
    public string Header => Role switch
    {
        ChatRole.User => $"你 · {Timestamp:HH:mm}",
        ChatRole.Assistant => $"Agent · {Timestamp:HH:mm}",
        _ => Timestamp.ToString("HH:mm")
    };

    public IBrush BubbleBrush => Role switch
    {
        ChatRole.User => ChatPalette.UserBubble,
        ChatRole.Assistant => ChatPalette.Bubble,
        _ => Brushes.Transparent
    };

    public IBrush BodyBrush => Role switch
    {
        ChatRole.User => ChatPalette.UserText,
        ChatRole.Assistant => ChatPalette.Answer,
        _ => ChatPalette.SystemText
    };

    public HorizontalAlignment BubbleAlignment =>
        Role == ChatRole.User ? HorizontalAlignment.Right : HorizontalAlignment.Left;

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
    public ChatToolCall AddToolCall(string callId, string name, string arguments)
    {
        if (Parts.Count == 0 || Parts[^1] is not ChatToolCallPart part)
        {
            part = new ChatToolCallPart();
            Parts.Add(part);
            this.RaisePropertyChanged(nameof(HasParts));
        }

        return part.Add(callId, name, arguments);
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
