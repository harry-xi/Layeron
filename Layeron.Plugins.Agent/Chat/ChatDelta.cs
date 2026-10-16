namespace Layeron.Plugins.Agent;

/// <summary>
/// One event out of a streaming reply.
/// <para>
/// A reply is not a string. It is a sequence of things the model produced - prose, its reasoning, and
/// later the tools it asked for - and the panel has to be able to tell them apart to render them
/// differently. Modelling that as a closed hierarchy means a new kind of output is a new record type and
/// one more arm in the panel's switch, not a change to the session contract.
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
