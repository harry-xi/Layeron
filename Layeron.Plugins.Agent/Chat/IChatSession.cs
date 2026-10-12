namespace Layeron.Plugins.Agent;

/// <summary>
/// The seam between the chat panel and whatever actually answers a message.
/// <para>
/// Deliberately the smallest possible contract: the panel hands over the text, the attachments and this
/// session's options, and gets text back. Nothing here mentions a provider, a model id or an HTTP shape -
/// an OpenAI-compatible client, a local runtime and a scripted fake all implement this one method, and the
/// panel cannot tell them apart.
/// </para>
/// </summary>
public interface IChatSession
{
    /// <summary>
    /// Sends one turn and returns the reply text.
    /// <para>
    /// The implementation must honour <paramref name="ct"/>: the panel cancels a turn the user abandons,
    /// and a provider call that ignores cancellation keeps a socket (and the plugin's load context) alive.
    /// </para>
    /// </summary>
    Task<string> SendAsync(
        string text,
        IReadOnlyList<AgentAttachment> attachments,
        AgentOptions options,
        CancellationToken ct);
}

/// <summary>
/// Stand-in session used until a real provider is wired up: it echoes the turn back so the panel is
/// usable end to end (compose, attach, send, cancel, transcript) without a network call.
/// </summary>
internal sealed class EchoChatSession : IChatSession
{
    public async Task<string> SendAsync(
        string text,
        IReadOnlyList<AgentAttachment> attachments,
        AgentOptions options,
        CancellationToken ct)
    {
        // A real provider would stream; waiting briefly here makes the busy state and the cancel button
        // observable instead of flashing past on a completed task.
        await Task.Delay(TimeSpan.FromMilliseconds(400), ct).ConfigureAwait(false);

        var parts = new List<string>();
        if (text.Length > 0)
            parts.Add(text);

        foreach (var attachment in attachments)
            parts.Add($"（{attachment.DisplayText}）");

        return parts.Count == 0
            ? "（空消息）"
            : $"已收到：{string.Join(" ", parts)}";
    }
}
