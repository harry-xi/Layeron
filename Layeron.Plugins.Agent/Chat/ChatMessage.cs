using Avalonia.Layout;
using Avalonia.Media;

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
/// One message in the conversation. Immutable on purpose: the transcript is an append-only log, so a
/// message that could be edited later would make "what did the agent actually see" unanswerable.
/// <para>
/// Alignment and brushes are view facts rather than agent facts, and they live here instead of in a
/// converter because the bubble is the only thing that varies by role.
/// </para>
/// </summary>
public sealed class ChatMessage
{
    private static readonly IBrush UserBubble = new SolidColorBrush(Color.Parse("#2D6CDF"));
    private static readonly IBrush AssistantBubble = new SolidColorBrush(Color.Parse("#3A3A3A"));
    private static readonly IBrush SystemBubble = Brushes.Transparent;
    private static readonly IBrush UserText = Brushes.White;
    private static readonly IBrush AssistantText = new SolidColorBrush(Color.Parse("#E6E6E6"));
    private static readonly IBrush SystemText = new SolidColorBrush(Color.Parse("#909090"));

    public required ChatRole Role { get; init; }

    /// <summary>Message body. Empty is valid when the message only carries attachments.</summary>
    public string Text { get; init; } = "";

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    /// <summary>
    /// What rode along with this message. The same <see cref="AgentAttachment"/> instances the input area
    /// produced are kept, so the payload a backend receives is the object the user attached rather than a
    /// label re-parsed out of text.
    /// </summary>
    public IReadOnlyList<AgentAttachment> Attachments { get; init; } = [];

    public bool HasText => Text.Length > 0;

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

    /// <summary>Creates a system notice.</summary>
    public static ChatMessage Notice(string text) => new() { Role = ChatRole.System, Text = text };
}
