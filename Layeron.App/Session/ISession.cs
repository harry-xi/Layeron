using System;
using Layeron.App.ViewModels;

namespace Layeron.App.Session;

/// <summary>
/// One live imaging session. Sessions have a real lifetime: they are created and
/// closed at runtime through <see cref="ISessionManager"/>, and every consumer
/// (dockable, future plugin) must react to that lifetime instead of assuming a
/// session lives for the whole app run.
/// </summary>
public interface ISession
{
    /// <summary>Current main volume and crosshair position.</summary>
    SharedDataSourceViewModel SharedData { get; }

    /// <summary>Mask overlay layers bound to the current main volume.</summary>
    MaskManagerViewModel MaskManager { get; }

    /// <summary>Annotation layers and the interaction state for drawing them.</summary>
    AnnotationManagerViewModel AnnotationManager { get; }

    /// <summary>Shared window/level lookup applied by every slice view.</summary>
    WindowLevelViewModel WindowLevel { get; }

    /// <summary>
    /// Raised first while the session is closing. The session is still fully usable
    /// here; consumers detach event handlers and persist any session-bound state.
    /// </summary>
    event Action<ISession>? Closing;

    /// <summary>Raised last. After this the session must no longer be used.</summary>
    event Action<ISession>? Closed;
}
