namespace Layeron.Plugins.Agent;

/// <summary>
/// One thing the user said: the text, plus whatever rode along with it.
/// <para>
/// A turn is an object rather than a parameter list because it is what a backend actually receives, and
/// what belongs to a turn will keep growing - an image, a region of interest, the result of a tool this
/// plugin called - without every one of those becoming another parameter on
/// <see cref="IChatSession.StreamAsync"/>.
/// </para>
/// </summary>
public sealed record ChatTurn
{
    /// <summary>The message text. May be empty when the turn is only attachments.</summary>
    public required string Text { get; init; }

    /// <summary>
    /// The attachments the user queued, as the panel produced them. The <em>objects</em> travel, not a
    /// re-parsed caption: the payload is what a backend can actually consume.
    /// </summary>
    public IReadOnlyList<AgentAttachment> Attachments { get; init; } = [];
}
