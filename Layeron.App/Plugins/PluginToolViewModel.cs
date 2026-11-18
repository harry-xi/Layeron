using System;
using Dock.Model.ReactiveUI.Controls;

namespace Layeron.App.Plugins;

/// <summary>
/// Docks a control created by a plugin.
/// <para>
/// The host only ever holds the content as <see cref="object"/>: it never names a plugin type, so a
/// plugin's view model and views stay reachable only from the plugin's own load context. Disposing
/// this view model disposes the content, which is the plugin's chance to detach from host services.
/// </para>
/// <para>
/// The content is built on first read, not when the dockable is created. A restored layout creates
/// every panel in it - including the hidden tabs - and a plugin's control may do real work in its
/// constructor (starting an interpreter, opening a model, constructing a large view). Building it
/// only when its tab is first shown keeps that work out of the startup path; a panel that is never
/// looked at is never built.
/// </para>
/// </summary>
public sealed class PluginToolViewModel : Tool, IDisposable
{
    private readonly Func<object> _contentFactory;
    private object? _content;

    public PluginToolViewModel(string title, string typeId, object content, bool canClose = true)
        : this(title, typeId, () => content, canClose)
    {
    }

    /// <param name="contentFactory">Builds the plugin's control the first time it is displayed.</param>
    public PluginToolViewModel(string title, string typeId, Func<object> contentFactory, bool canClose = true)
    {
        Title = title;
        Id = typeId;
        CanClose = canClose;
        CanDockAsDocument = false;
        TypeId = typeId;
        _contentFactory = contentFactory;
    }

    /// <summary>Stable dockable type id, used by the layout serializer to restore this panel.</summary>
    public string TypeId { get; }

    /// <summary>The plugin-provided control, created on first use.</summary>
    public object Content => _content ??= _contentFactory();

    public void Dispose()
    {
        // Closing the panel must release the plugin's handlers even while the plugin stays loaded.
        // Nothing is created on dispose: a panel that was never shown has nothing to release.
        if (_content is IDisposable disposable)
            disposable.Dispose();
        _content = null;
    }
}
