using System.Runtime.CompilerServices;
using Layeron.Plugin.Abstractions;
using Layeron.Scripting.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using AgentChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AgentChatRole = Microsoft.Extensions.AI.ChatRole;
using HistoryMessage = Layeron.Plugins.Agent.ChatMessage;
using HistoryRole = Layeron.Plugins.Agent.ChatRole;

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
/// therefore apply from the next session, which is why opening another saved conversation also drops the
/// session rather than carrying the old history into the new transcript.
/// </para>
/// <para>
/// The stream is the framework's own <see cref="AgentResponseUpdate"/> rather than a panel-shaped event
/// type. Tool approvals are part of that shape and have to travel back through the same session, so a
/// private translation vocabulary would only be another set of events to keep in step.
/// </para>
/// </summary>
internal sealed class ChatSession(
    IPluginContext context,
    AgentOptions options,
    IPluginServices services,
    IScriptHostService scriptHost,
    IReadOnlyList<HistoryMessage>? restoredHistory = null) : IChatSession
{
    /// <summary>
    /// Cancelled when the session is disposed, so a turn still in flight when the panel closes is torn
    /// down here rather than left to finish against a socket nobody is waiting for.
    /// </summary>
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// The transcript this session was opened on, converted once when the first turn creates the agent
    /// session. A saved conversation is not just a display: continuing it has to tell the model what was
    /// already said, or the restored transcript would be a lie about the context of the next reply.
    /// </summary>
    private List<AgentChatMessage> _pendingHistory = ToAgentHistory(restoredHistory ?? []);

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
            if (_conversation is null)
            {
                _conversation = await agent.CreateSessionAsync(token).ConfigureAwait(false);

                if (_pendingHistory.Count > 0)
                {
                    // The provider is an in-memory history store, so seeding it is what turns a restored
                    // transcript into a conversation the model can continue. It is seeded once: after
                    // this, the framework records every new turn into the same session.
                    _conversation.SetInMemoryChatHistory(_pendingHistory);
                    _pendingHistory = [];
                }
            }

            await foreach (var update in agent
                           .RunStreamingAsync(message, _conversation, options: null, token)
                           .ConfigureAwait(false))
            {
                foreach (var content in update.Contents)
                    _log.Verbose("Agent update: {Content}", ChatFormat.Describe(content));

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
        var client = ChatClientFactory.Create(options).AsIChatClient();

        var chatOptions = new ChatOptions
        {
            Instructions = options.SystemPrompt,
            Temperature = (float)options.Temperature,
            MaxOutputTokens = (int)options.MaxTokens,
            Tools = new ChatTools(context, services, scriptHost).BuildTools(),
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

        // The decorator sits below the function-invoking client, so a tool's ImageToolResult is still an
        // object when it reaches this point and can be turned into real image content. See
        // ToolImageChatClient for why the provider cannot be handed the result as it is.
        var agent = new ToolImageChatClient(client).AsAIAgent(agentOptions);
        _injector = agent.GetService<MessageInjectingChatClient>();

        if (_injector is null)
            _log.Warn("Agent 未提供消息注入客户端，执行中的指导将不可用。");

        return agent;
    }

    /// <summary>
    /// Turns a saved transcript into the messages the in-memory history provider expects.
    /// <para>
    /// The transcript is a display model - prose, reasoning, calls and results interleaved - while a chat
    /// history is a sequence of messages. Reasoning is dropped because it shaped the previous answer rather
    /// than being something the user said, and a tool result travels in its own tool message so the next
    /// model call sees a complete call/result pair instead of a dangling function request.
    /// </para>
    /// </summary>
    internal static List<AgentChatMessage> ToAgentHistory(IReadOnlyList<HistoryMessage> history)
    {
        var messages = new List<AgentChatMessage>(history.Count);

        foreach (var message in history)
        {
            switch (message.Role)
            {
                case HistoryRole.User:
                    var text = Compose(new ChatTurn { Text = message.Body, Attachments = message.Attachments });
                    if (text.Length > 0)
                    {
                        messages.Add(new AgentChatMessage(
                            AgentChatRole.User,
                            message.IsGuidance ? GuidancePrefix + text : text));
                    }
                    break;

                case HistoryRole.Assistant:
                    AppendAssistantHistory(messages, message);
                    break;

                // A notice is the panel speaking to the user, not something the model ever said or saw.
            }
        }

        return messages;
    }

    private static void AppendAssistantHistory(List<AgentChatMessage> messages, HistoryMessage message)
    {
        var assistantContents = new List<AIContent>();
        var resultContents = new List<AIContent>();

        void Flush()
        {
            if (assistantContents.Count > 0)
            {
                messages.Add(new AgentChatMessage(AgentChatRole.Assistant, assistantContents));
                assistantContents = [];
            }

            if (resultContents.Count > 0)
            {
                messages.Add(new AgentChatMessage(AgentChatRole.Tool, resultContents));
                resultContents = [];
            }
        }

        foreach (var part in message.Parts)
        {
            switch (part)
            {
                case ChatReasoningPart:
                    // The model's private working is not part of the conversation it needs replayed.
                    break;

                case ChatToolCallPart calls:
                    foreach (var call in calls.Calls)
                    {
                        // A call with no result is a turn that never finished; replaying only the request
                        // would leave the provider with a function call nobody answered.
                        if (!call.Completed)
                            continue;

                        // The transcript kept the arguments as display text, not as the provider's
                        // dictionary. An empty dictionary is the honest replay: the call's name and its
                        // result are the parts that let the model continue; inventing parsed arguments
                        // from "key=value" text would be worse than omitting them.
                        assistantContents.Add(new FunctionCallContent(
                            call.CallId,
                            call.Name,
                            new Dictionary<string, object?>()));
                        resultContents.Add(new FunctionResultContent(call.CallId, call.Result));
                    }

                    Flush();
                    break;

                default:
                    if (part.Body.Length > 0)
                        assistantContents.Add(new TextContent(part.Body));
                    break;
            }
        }

        Flush();
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
