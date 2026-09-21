using System;
using System.Reactive.Subjects;
using Layeron.App.Logging;
using Layeron.App.ViewModels;

namespace Layeron.App.Session;

/// <summary>
/// Default <see cref="ISessionManager"/>. Session creation and destruction always
/// flow through the <see cref="CurrentSession"/> stream, so dockables and plugins
/// are reliably notified.
/// </summary>
public sealed class SessionManager : ISessionManager
{
    private readonly BehaviorSubject<ISession?> _current = new(null);

    // Window/level view state is per-app, not per-session: one lookup-table instance
    // is re-pointed at each session's data source so every slice keeps a stable reference.
    private readonly WindowLevelViewModel _windowLevel = new();

    public IObservable<ISession?> CurrentSession => _current;

    public ISession? Current => _current.Value;

    public ISession CreateSession()
    {
        if (_current.Value is { } existing)
        {
            Log.Info("Session", "Replacing session {SessionId}", existing.GetHashCode());
            CloseSession();
        }

        var sharedData = new SharedDataSourceViewModel();
        var maskManager = new MaskManagerViewModel();

        sharedData.MaskManager = maskManager;
        maskManager.SharedData = sharedData;
        _windowLevel.SharedData = sharedData;

        var session = new SessionContext(sharedData, maskManager, _windowLevel);
        _current.OnNext(session);
        Log.Debug("Session", "Session {SessionId} created", session.GetHashCode());
        return session;
    }

    public void CloseSession()
    {
        // The manager only ever creates SessionContext instances.
        if (_current.Value is not SessionContext session)
            return;

        // Two-phase shutdown: consumers detach and save while the session is still
        // fully usable (Closing), rebind to an empty state (null), then get the final
        // notice. Nothing may be used after Closed.
        Log.Debug("Session", "Closing session {SessionId} (phase 1: consumers detach)", session.GetHashCode());
        session.RaiseClosing();
        _current.OnNext(null);

        // The window/level view state outlives the session; detach it so nothing
        // keeps pointing into a closed session.
        _windowLevel.SharedData = null;

        session.RaiseClosed();
        Log.Debug("Session", "Session {SessionId} closed (phase 2 complete)", session.GetHashCode());
    }
}
