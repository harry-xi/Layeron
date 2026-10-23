using System;
using Layeron.App.ViewModels;

namespace Layeron.App.Session;

/// <summary>
/// The single source of truth for one live session's core data. A session has a real
/// lifetime: it is created and closed through <see cref="ISessionManager"/>, and
/// consumers (dockables, future plugins) must react to that lifetime instead of
/// holding a captured object graph. This keeps the dock layout serializable (layout
/// records only identifiers, never live objects) and gives plugins one stable entry
/// point into the core data.
/// </summary>
public sealed class SessionContext : ISession
{
    public SessionContext(
        SharedDataSourceViewModel sharedData,
        MaskManagerViewModel maskManager,
        AnnotationManagerViewModel annotationManager,
        WindowLevelViewModel windowLevel)
    {
        SharedData = sharedData;
        MaskManager = maskManager;
        AnnotationManager = annotationManager;
        WindowLevel = windowLevel;
    }

    /// <summary>Current main volume and crosshair position.</summary>
    public SharedDataSourceViewModel SharedData { get; }

    /// <summary>Mask overlay layers bound to the current main volume.</summary>
    public MaskManagerViewModel MaskManager { get; }

    /// <summary>Annotation layers and the interaction state for drawing them.</summary>
    public AnnotationManagerViewModel AnnotationManager { get; }

    /// <summary>Shared window/level lookup applied by every slice view.</summary>
    public WindowLevelViewModel WindowLevel { get; }

    /// <inheritdoc />
    public event Action<ISession>? Closing;

    /// <inheritdoc />
    public event Action<ISession>? Closed;

    /// <summary>First phase of shutdown: the session is still fully usable here.</summary>
    internal void RaiseClosing() => Closing?.Invoke(this);

    /// <summary>Final phase of shutdown: nothing may use the session after this.</summary>
    internal void RaiseClosed() => Closed?.Invoke(this);
}