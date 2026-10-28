using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AIChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Layeron.Plugins.Agent;

/// <summary>
/// The seam between the chat panel and whatever actually answers a message.
/// <para>
/// A session is shorter-lived than the panel. It is a conversation, not a connection: the panel creates one
/// when it starts or restores a conversation and disposes it when the user switches to another one or closes
/// the panel, so a session that is finished with is finitely released - a provider call holding a socket
/// would otherwise pin the plugin's load context for the life of the process.
/// </para>
/// <para>
/// The reply is a stream of the framework's own updates rather than a string or a panel-specific event
/// type. Those updates already carry prose, reasoning, tool calls and their results - and the approval
/// requests a human-in-the-loop tool call produces - so the panel can render exactly what happened, in the
/// order it happened, without a second vocabulary to maintain.
/// </para>
/// <para>
/// Nothing here mentions a provider or an HTTP shape - an OpenAI-compatible client, a local runtime and a
/// scripted fake all implement these methods, and the panel cannot tell them apart.
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
    IAsyncEnumerable<AgentResponseUpdate> StreamAsync(ChatTurn turn, CancellationToken ct);

    /// <summary>
    /// Continues the conversation after the user answered one or more approval requests.
    /// <para>
    /// The responses must belong to requests surfaced by the same session: the agent binds them to the
    /// requests it recorded, and an unbound response is ignored rather than allowed to approve a call
    /// nobody was asked about.
    /// </para>
    /// </summary>
    IAsyncEnumerable<AgentResponseUpdate> StreamApprovalsAsync(
        IReadOnlyList<ToolApprovalResponseContent> responses,
        CancellationToken ct);

    /// <summary>
    /// Sends guidance into a run that is already in progress.
    /// <para>
    /// The message is queued for the agent's next service call rather than starting a new turn: that is
    /// what lets the user correct a direction, add a constraint or stop a bad tool call while the model is
    /// working. It must be called while a run is active; the queue belongs to the session, so a guidance
    /// sent during an approval wait is picked up when the run resumes.
    /// </para>
    /// <para>
    /// A turn is accepted here too, not just text, so an attachment queued while the agent is working is
    /// described to the model by the same rules as an ordinary turn.
    /// </para>
    /// </summary>
    Task GuideAsync(ChatTurn turn, CancellationToken ct);
}

/// <summary>
/// Stand-in session used until a real provider is wired up: it echoes the turn back so the panel is
/// usable end to end (compose, attach, send, cancel, transcript) without a network call.
/// </summary>
internal sealed class EchoChatSession : IChatSession
{
    public async IAsyncEnumerable<AgentResponseUpdate> StreamAsync(
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

        yield return new AgentResponseUpdate
        {
            Contents =
            [
                new TextContent(parts.Count == 0
                    ? "（空消息）"
                    : $"已收到：{string.Join(" ", parts)}")
            ],
            Role = AIChatRole.Assistant
        };
    }

    public async IAsyncEnumerable<AgentResponseUpdate> StreamApprovalsAsync(
        IReadOnlyList<ToolApprovalResponseContent> responses,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // The echo backend has no tools, so it can never have produced a request to answer.
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    public Task GuideAsync(ChatTurn turn, CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
