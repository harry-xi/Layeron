namespace Layeron.Plugins.Agent;

/// <summary>
/// One event out of a streaming reply.
/// <para>
/// A reply is not a string. It is a sequence of things the model produced - prose, its reasoning, and the
/// tools it asked for with what they returned - and the panel has to be able to tell them apart to render
/// them differently. Modelling that as a closed hierarchy means a new kind of output is a new record type
/// and one more arm in the panel's switch, not a change to the session contract.
/// </para>
/// <para>
/// Deliberately a stream rather than a finished reply: the same shape serves a token-at-a-time provider
/// and one that answers in a single chunk, and it is what makes cancellation mean "stop generating"
/// rather than "throw away what already arrived".
/// </para>
/// </summary>
public abstract record ChatDelta;

/// <summary>Model prose. Consecutive deltas concatenate into the answer.</summary>
public sealed record ChatTextDelta(string Text) : ChatDelta;

/// <summary>
/// The model's reasoning, where the provider exposes it. Kept apart from <see cref="ChatTextDelta"/>
/// because it is not the answer: generated output that the user must be able to read or ignore as a
/// separate thing.
/// </summary>
public sealed record ChatReasoningDelta(string Text) : ChatDelta;

/// <summary>
/// A tool the model asked to run.
/// <para>
/// Separate from <see cref="ChatTextDelta"/> because it is not the answer and must not read as one: the
/// panel shows the call as a request with its own arguments. <see cref="CallId"/> is the only thing
/// <see cref="ChatToolResultDelta"/> can be matched against, so it travels even though the panel never
/// shows it.
/// </para>
/// </summary>
/// <param name="CallId">Identifies the call, so its result can be matched to it.</param>
/// <param name="Name">The tool's name as the model asked for it.</param>
/// <param name="Arguments">The arguments rendered to text; empty when the tool takes none.</param>
public sealed record ChatToolCallDelta(string CallId, string Name, string Arguments) : ChatDelta;

/// <summary>
/// What a tool call returned. Arrives as its own event because the call and its outcome are produced at
/// different moments - and a call that never gets one is a call the panel can show as still running.
/// </summary>
/// <param name="CallId">Matches the <see cref="ChatToolCallDelta.CallId"/> this is the outcome of.</param>
/// <param name="Result">The outcome rendered to text; may be empty for a tool that returned nothing.</param>
/// <param name="Failed">True when the call raised instead of returning.</param>
public sealed record ChatToolResultDelta(string CallId, string Result, bool Failed) : ChatDelta;
