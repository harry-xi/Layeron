using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Layeron.Plugin.Abstractions;
using Layeron.Scripting.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

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
/// This is the only type in the plugin that references the framework, and it never mentions Avalonia.
/// </para>
/// </summary>
internal sealed class ChatSession(AgentOptions options, IPluginServices services, IScriptHostService seciptHost) : IChatSession
{
    /// <summary>
    /// Cancelled when the session is disposed, so a turn still in flight when the panel closes is torn
    /// down here rather than left to finish against a socket nobody is waiting for.
    /// </summary>
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    
    private AIAgent? _agent;
    private AgentSession? _conversation;
    private bool _disposed;

    private readonly IPluginLog _log = services.Log;
    
    public async IAsyncEnumerable<ChatDelta> StreamAsync(
        ChatTurn turn,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(turn);

        if (turn.Text.Length == 0 && turn.Attachments.Count == 0)
            yield break;

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

            // The framework rejects an empty message, so a turn that is only attachments is described as
            // text. Until attachments can be uploaded, this is also the only honest thing to send.
            var message = Compose(turn);

            await foreach (var update in agent.RunStreamingAsync(message, _conversation, options: null, token)
                           .ConfigureAwait(false))
            {
                foreach (var delta in Translate(update,services.Log))
                    yield return delta;
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
            Tools = new ChatTools(services,seciptHost).BuildTools()
        };

        var agentOptions = new ChatClientAgentOptions
        {
            Id = AgentPlugin.ChatToolId,
            Name = "Layeron Agent",
            ChatOptions = chatOptions,
            ChatHistoryProvider = new InMemoryChatHistoryProvider(),
        };

        _log.Info("Agent 已连接 {Endpoint}，模型 {Model}。", options.Endpoint, options.Model);

        return client.AsAIAgent(agentOptions);
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

    /// <summary>
    /// Turns one framework update into panel deltas. Reasoning, prose and tool traffic each travel as their
    /// own kind so the panel can render them apart instead of concatenating everything into one paragraph.
    /// </summary>
    internal static IEnumerable<ChatDelta> Translate(AgentResponseUpdate update, IPluginLog logger)
    {
        foreach (var content in update.Contents)
        {
            logger.Debug("Agent update: {Content}", Describe(content));

            switch (content)
            {
                case TextReasoningContent { Text.Length: > 0 } reasoning:
                    yield return new ChatReasoningDelta(reasoning.Text);
                    break;

                case TextContent { Text.Length: > 0 } text:
                    yield return new ChatTextDelta(text.Text);
                    break;

                // The call and its result are two events, not one, because they happen at different moments: a
                // call that has no result yet is a call still running, which the panel can show and a single
                // combined event could not. The id is the only thing that joins the two.
                case FunctionCallContent call:
                    yield return new ChatToolCallDelta(call.CallId, call.Name, DescribeArguments(call.Arguments));
                    break;

                case FunctionResultContent result:
                    yield return new ChatToolResultDelta(
                        result.CallId,
                        DescribeResult(result.Result),
                        result.Exception is not null);
                    break;
            }
        }
    }

    /// <summary>
    /// One content, as a line worth logging: an <see cref="AIContent"/>'s type name says nothing about what
    /// the model asked for, which is the one thing a tool call needs to be diagnosable.
    /// </summary>
    private static string Describe(AIContent content) => content switch
    {
        FunctionCallContent call => $"{nameof(FunctionCallContent)} {call.Name}({DescribeArguments(call.Arguments)})",
        FunctionResultContent result => $"{nameof(FunctionResultContent)} {DescribeResult(result.Result)}",
        _ => content.GetType().Name
    };

    /// <summary>
    /// Renders a call's arguments to text. Values are whatever the provider decoded - a
    /// <see cref="JsonElement"/> for an OpenAI-shaped call, a CLR value for a locally built one - so raw JSON
    /// is preferred where there is one and anything else is rendered as-is.
    /// </summary>
    private static string DescribeArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return "";

        return string.Join(", ", arguments.Select(pair => $"{pair.Key}={DescribeValue(pair.Value)}"));
    }

    private static string DescribeResult(object? result) => result switch
    {
        null => "",
        string text => Truncate(text),
        _ => DescribeValue(result)
    };

    private static string DescribeValue(object? value) => value switch
    {
        null => "null",
        JsonElement element => Truncate(element.GetRawText()),
        IFormattable formattable => Truncate(formattable.ToString(null, CultureInfo.InvariantCulture)),
        _ => Truncate(value.ToString() ?? "")
    };

    /// <summary>
    /// Both the transcript row and the log line carry argument and result text, and neither should be able to
    /// grow a chat bubble to the size of a file because a tool returned one.
    /// </summary>
    private static string Truncate(string text) => text.Length <= 400 ? text : text[..400] + "…";
}
