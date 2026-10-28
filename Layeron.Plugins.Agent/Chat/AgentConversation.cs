namespace Layeron.Plugins.Agent;

/// <summary>
/// One saved conversation: the transcript the panel draws plus the small amount of metadata the history
/// window needs to list it.
/// <para>
/// The id is the file name and never changes. A conversation that is restored and continued is the same
/// conversation, so its entry moves to the top of the history list instead of becoming a second one.
/// </para>
/// </summary>
public sealed class AgentConversation
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;

    public IReadOnlyList<ChatMessage> Messages { get; init; } = [];
}

/// <summary>
/// What the history window lists. Deliberately small: a management list should not have to read every
/// transcript on disk, so the store keeps this in a separate index.
/// </summary>
public sealed record AgentConversationSummary(
    string Id,
    string Title,
    DateTimeOffset UpdatedAt,
    int MessageCount)
{
    /// <summary>Local, to the minute: the history list is a "when did we talk" view, not a log.</summary>
    public string UpdatedText => UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string MessageCountText => $"{MessageCount} 条";
}
