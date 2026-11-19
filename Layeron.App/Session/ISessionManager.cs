using System;
using Layeron.App.Tools;

namespace Layeron.App.Session;

/// <summary>
/// Tracks the current session and broadcasts its lifetime. Consumers never hold a
/// session object graph captured at build time; they subscribe to
/// <see cref="CurrentSession"/> and rebind (or clear) when it emits.
/// </summary>
public interface ISessionManager
{
    /// <summary>The current session, or null while no session is open.</summary>
    ISession? Current { get; }

    /// <summary>
    /// Stream of session changes: replays the current value on subscribe, emits the
    /// new session on creation and null on close. The replay makes it safe for
    /// dockables that are created (or restored from layout) after the session.
    /// </summary>
    IObservable<ISession?> CurrentSession { get; }

    /// <summary>Opens a new session, closing any current one first.</summary>
    ISession CreateSession(ToolCatalog? toolCatalog = null);

    /// <summary>Closes the current session, notifying consumers before it becomes unusable.</summary>
    void CloseSession();
}
