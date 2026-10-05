using System.Threading;

namespace Layeron.Scripting.Abstractions;

/// <summary>
/// Per-execution state a script needs but cannot create itself: how to cancel, where to report progress
/// and status, and how to get back onto the host's UI thread.
/// <para>
/// Session objects - the mask manager, dock contributions - belong to the UI thread. A script that
/// computes on a background thread therefore must not touch them directly, so the write helpers on
/// <see cref="ScriptGlobals"/> route their effects through <see cref="Invoke"/>. The script host supplies
/// it, because the host is the side that knows which UI framework is in play.
/// </para>
/// </summary>
public sealed class ScriptRunContext
{
    /// <summary>
    /// Context for an execution with no UI and no cancellation: a script activating as a plugin. Its
    /// <see cref="Invoke"/> runs inline, which is correct because activation already happens on the UI thread.
    /// </summary>
    public static ScriptRunContext None { get; } = new();

    /// <summary>Cancelled when this run is superseded, or the editor tab closes.</summary>
    public CancellationToken Cancellation { get; init; }

    /// <summary>Receives transient status lines. Null when nothing is listening.</summary>
    public Action<string>? Status { get; init; }

    /// <summary>Receives progress fractions in [0, 1]. Null when nothing is listening.</summary>
    public Action<double>? Progress { get; init; }

    /// <summary>
    /// Runs an action on the host's UI thread. The default runs it inline, which is right when the caller
    /// is already on that thread - activation - or when there is no UI at all - tests.
    /// </summary>
    public Action<Action> Invoke { get; init; } = static action => action();
}
