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
/// the exception, because a reply arrives as a stream - the bubble is appended on the first token and then
/// grown in place, which is the difference between watching the model write and watching a spinner.
/// </para>
/// <para>
/// Alignment and brushes are view facts rather than agent facts, and they live here instead of in a
/// converter because the bubble is the only thing that varies by role.
/// </para>
/// </summary>
public sealed class ChatMessage : ReactiveObject
{
    private static readonly IBrush UserBubble = new SolidColorBrush(Color.Parse("#2D6CDF"));
    private static readonly IBrush AssistantBubble = new SolidColorBrush(Color.Parse("#3A3A3A"));
    private static readonly IBrush SystemBubble = Brushes.Transparent;
    private static readonly IBrush UserText = Brushes.White;
    private static readonly IBrush AssistantText = new SolidColorBrush(Color.Parse("#E6E6E6"));
    private static readonly IBrush SystemText = new SolidColorBrush(Color.Parse("#909090"));

    private string _text = "";
    private string _reasoning = "";

    public required ChatRole Role { get; init; }

    /// <summary>Message body. Empty is valid when the message only carries attachments.</summary>
    public string Text
    {
        get => _text;

        // Settable only inside this assembly: a message's text is composed by the panel (a user line) or
        // grown by the panel (a streamed reply), and nothing outside has any business rewriting either.
        internal set
        {
            if (_text == value)
                return;

            _text = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(HasText));
        }
    }

    /// <summary>
    /// The model's reasoning, when the provider exposes it. Kept separate from <see cref="Text"/> so the
    /// view can offer it as a collapsible block rather than gluing it onto the answer.
    /// </summary>
    public string Reasoning
    {
        get => _reasoning;
        private set
        {
            if (_reasoning == value)
                return;

            _reasoning = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(HasReasoning));
        }
    }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    /// <summary>
    /// What rode along with this message. The same <see cref="AgentAttachment"/> instances the input area
    /// produced are kept, so the payload a backend receives is the object the user attached rather than a
    /// label re-parsed out of text.
    /// </summary>
    public IReadOnlyList<AgentAttachment> Attachments { get; init; } = [];

    public bool HasText => Text.Length > 0;

    public bool HasReasoning => Reasoning.Length > 0;

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
        ChatRole.User => UserBubble,
        ChatRole.Assistant => AssistantBubble,
        _ => SystemBubble
    };

    public IBrush TextBrush => Role switch
    {
        ChatRole.User => UserText,
        ChatRole.Assistant => AssistantText,
        _ => SystemText
    };

    public HorizontalAlignment BubbleAlignment =>
        Role == ChatRole.User ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    /// <summary>Appends streamed answer text to this message.</summary>
    public void Append(string text) => Text += text;

    /// <summary>Appends streamed reasoning to this message.</summary>
    public void AppendReasoning(string text) => Reasoning += text;

    /// <summary>Creates a system notice.</summary>
    public static ChatMessage Notice(string text) => new() { Role = ChatRole.System, Text = text };
}
