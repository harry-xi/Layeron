using Layeron.Plugin.Abstractions;

namespace Layeron.Plugins.Agent;

/// <summary>
/// The seam between the chat panel and whatever actually answers a message.
/// <para>
/// A session is shorter-lived than the panel. It is a conversation, not a connection: the panel creates one
/// when it is opened or cleared and disposes it when the user closes the panel, so a session that is
/// finished with is finitely released - a provider call holding a socket would otherwise pin the plugin's
/// load context for the life of the process.
/// </para>
/// <para>
/// The reply is an event stream rather than a string, and this is the whole reason the contract is worth
/// more than one method: it already carries prose, reasoning, and the tool calls with their results, and
/// it is where the user's approval of a call will have to go back. A <c>Task&lt;string&gt;</c> would have
/// had to be replaced to say any of that.
/// </para>
/// <para>
/// Nothing here mentions a provider, a model id or an HTTP shape - an OpenAI-compatible client, a local
/// runtime and a scripted fake all implement this one method, and the panel cannot tell them apart.
/// </para>
/// </summary>
public interface IChatSession : IAsyncDisposable
{
    /// <summary>
    /// Sends one turn and streams the reply. The sequence ends when the reply does; a caller that stops
    /// enumerating early cancels the rest.
    /// <para>
    /// The implementation must honour <paramref name="ct"/>: the panel cancels a turn the user abandons,
    /// and a provider call that ignores cancellation keeps a socket (and the plugin's load context) alive.
    /// </para>
    /// </summary>
    IAsyncEnumerable<ChatDelta> StreamAsync(ChatTurn turn, CancellationToken ct);
}

/// <summary>
/// Stand-in session used until a real provider is wired up: it echoes the turn back so the panel is
/// usable end to end (compose, attach, send, cancel, transcript) without a network call.
/// </summary>
internal sealed class EchoChatSession : IChatSession
{
    public async IAsyncEnumerable<ChatDelta> StreamAsync(
        ChatTurn turn,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // A real provider would stream; waiting briefly here makes the busy state and the cancel button
        // observable instead of flashing past on a completed task.
        await Task.Delay(TimeSpan.FromMilliseconds(400), ct).ConfigureAwait(false);

        var parts = new List<string>();
        if (turn.Text.Length > 0)
            parts.Add(turn.Text);

        foreach (var attachment in turn.Attachments)
            parts.Add($"（{attachment.DisplayText}）");

        yield return new ChatTextDelta(parts.Count == 0
            ? "（空消息）"
            : $"已收到：{string.Join(" ", parts)}");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
