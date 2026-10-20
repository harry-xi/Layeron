using System.Runtime.CompilerServices;
using Layeron.Plugin.Abstractions;
using Layeron.Scripting.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using AgentChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AgentChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Layeron.Plugins.Agent.Chat;

/// <summary>
/// The real conversation backend: a Microsoft Agent Framework agent over an OpenAI-compatible endpoint.
/// <para>
/// One instance owns one agent and one conversation. The agent is built lazily on the first turn - not in
/// the constructor - because every reason to build it (endpoint, model, key) is a settings value the user
/// can still be editing, and because a panel that never sends a message must never open a socket.
/// </para>
/// <para>
/// Rebuilding is deliberately avoided once a conversation has started: an <see cref="AgentSession"/> holds
/// the transcript, and swapping the agent mid-conversation would swap the history owner with it. Settings
/// therefore apply from the next session, which is why clearing the transcript also drops the session.
/// </para>
/// <para>
/// The stream is the framework's own <see cref="AgentResponseUpdate"/> rather than a panel-shaped event
/// type. Tool approvals are part of that shape and have to travel back through the same session, so a
/// private translation vocabulary would only be another set of events to keep in step.
/// </para>
/// </summary>
internal sealed class ChatSession(IPluginContext context,AgentOptions options, IPluginServices services, IScriptHostService seciptHost) : IChatSession
{
    /// <summary>
    /// Cancelled when the session is disposed, so a turn still in flight when the panel closes is torn
    /// down here rather than left to finish against a socket nobody is waiting for.
    /// </summary>
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    
    private AIAgent? _agent;
    private AgentSession? _conversation;
    private MessageInjectingChatClient? _injector;
    private bool _disposed;

    private readonly IPluginLog _log = services.Log;
    
    public IAsyncEnumerable<AgentResponseUpdate> StreamAsync(ChatTurn turn, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(turn);

        if (turn.Text.Length == 0 && turn.Attachments.Count == 0)
            return Empty();

        // The framework rejects an empty message, so a turn that is only attachments is described as
        // text. Until attachments can be uploaded, this is also the only honest thing to send.
        return RunAsync(new AgentChatMessage(AgentChatRole.User, Compose(turn)), ct);
    }

    public IAsyncEnumerable<AgentResponseUpdate> StreamApprovalsAsync(
        IReadOnlyList<ToolApprovalResponseContent> responses,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(responses);

        if (responses.Count == 0)
            return Empty();

        // An approval is a user message carrying the decisions, not a new turn in the transcript: the
        // framework binds each response to the request it recorded in the same session and continues the
        // run that stopped to ask.
        var message = new AgentChatMessage(
            AgentChatRole.User,
            responses.Cast<AIContent>().ToList());

        return RunAsync(message, ct);
    }

    public async Task GuideAsync(ChatTurn turn, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(turn);

        if (turn.Text.Length == 0 && turn.Attachments.Count == 0)
            return;

        var injector = _injector;
        var conversation = _conversation;

        // The queue lives in the session, so guidance only means something while a conversation is
        // established. The panel only offers it while busy, which is the same window.
        if (injector is null || conversation is null)
            throw new InvalidOperationException("AI 尚未开始工作，暂时无法发送指导。");

        var message = new AgentChatMessage(
            AgentChatRole.User,
            $"{GuidancePrefix}{Compose(turn)}");

        await injector.EnqueueMessagesAsync(conversation, [message], ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The prefix that tells the model this is a mid-task correction rather than a new request. It is not
    /// shown in the transcript, which keeps the user's own words intact.
    /// </summary>
    internal const string GuidancePrefix = "用户在任务执行中的补充指导：";

    /// <summary>
    /// Runs one conversation step and yields its updates. A step is either a user turn or the continuation
    /// of one after an approval; both go through the same gate because both append to the same session.
    /// </summary>
    private async IAsyncEnumerable<AgentResponseUpdate> RunAsync(
        AgentChatMessage message,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var token = run.Token;

        // One run at a time: the agent carries the conversation in its session, and two overlapping runs
        // would both append to it. The panel sends one turn at a time; this is the assertion, not the rule.
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var agent = _agent ??= BuildAgent();

            // Created before the run, not after: the framework records the turn into the session it is
            // given, and a null session is a transient one it throws away - so a session obtained after
            // the call would be empty and the model would meet every message as the first.
            _conversation ??= await agent.CreateSessionAsync(token).ConfigureAwait(false);

            await foreach (var update in agent
                           .RunStreamingAsync(message, _conversation, options: null, token)
                           .ConfigureAwait(false))
            {
                foreach (var content in update.Contents)
                    _log.Debug("Agent update: {Content}", ChatFormat.Describe(content));

                yield return update;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Stop the turn in flight first: taking the gate would otherwise wait for exactly the run this is
        // meant to abort, and a provider that does not cancel promptly would hang Dispose.
        await _lifetime.CancelAsync().ConfigureAwait(false);

        await _gate.WaitAsync().ConfigureAwait(false);
        _gate.Release();

        // A collectible load context cannot go away while a framework object rooted in this assembly is
        // still reachable from a live conversation; the transcript is the one such reference.
        _conversation = null;
        _agent = null;
        _injector = null;

        _gate.Dispose();
        _lifetime.Dispose();
    }

    /// <summary>
    /// Builds the agent over one client. The client is configured from the settings as they are now, and
    /// the history provider is explicit rather than left to a default: the conversation is this object's
    /// entire reason to exist, so which store holds it should not be something a library version decides.
    /// </summary>
    private AIAgent BuildAgent()
    {
        var client = ChatClientFactory.Create(options);

        var chatOptions = new ChatOptions
        {
            Instructions = options.SystemPrompt,
            Temperature = (float)options.Temperature,
            MaxOutputTokens = (int)options.MaxTokens,
            Tools = new ChatTools(context,services,seciptHost).BuildTools(),
        };

        var agentOptions = new ChatClientAgentOptions
        {
            Id = AgentPlugin.ChatToolId,
            Name = "Layeron Agent",
            ChatOptions = chatOptions,
            ChatHistoryProvider = new InMemoryChatHistoryProvider(),
            AIContextProviders = [ChatSkills.BuildInSkills],
            // Guidance sent while a turn is running is queued in the session and picked up at the next
            // service call. Per-service-call persistence is the framework's recommendation for injection:
            // the injected message then survives from the service call that consumed it to the next one.
            EnableMessageInjection = true,
            RequirePerServiceCallChatHistoryPersistence = true,
        };

        _log.Info("Agent 已连接 {Endpoint}，模型 {Model}。", options.Endpoint, options.Model);

        var agent = client.AsAIAgent(agentOptions);
        _injector = agent.GetService<MessageInjectingChatClient>();

        if (_injector is null)
            _log.Warn("Agent 未提供消息注入客户端，执行中的指导将不可用。");

        return agent;
    }

    /// <summary>
    /// The text to send. Attachments are appended as lines the way a user would have typed them, because
    /// the one thing the model cannot see yet is what the user attached; when they become real content -
    /// an image, a file handle - they belong in the message as content parts, not in this string.
    /// </summary>
    private static string Compose(ChatTurn turn)
    {
        if (turn.Attachments.Count == 0)
            return turn.Text;

        var lines = new List<string>(turn.Attachments.Count + 1);
        if (turn.Text.Length > 0)
            lines.Add(turn.Text);

        lines.AddRange(turn.Attachments.Select(a => $"[附件] {a.TranscriptText}"));

        return string.Join('\n', lines);
    }

    private static async IAsyncEnumerable<AgentResponseUpdate> Empty()
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }
}
