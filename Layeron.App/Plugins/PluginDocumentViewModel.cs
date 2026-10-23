using System;
using Dock.Model.ReactiveUI.Controls;

namespace Layeron.App.Plugins;

/// <summary>
/// Hosts a document created by a plugin in the centre document dock.
/// <para>
/// This is the tab counterpart of <see cref="PluginToolViewModel"/> and exists for the same reason:
/// the host only ever holds the content as <see cref="object"/>, so it never names a plugin type and a
/// plugin's view model and views stay reachable only from the plugin's own load context. Disposing this
/// view model disposes the content, which is the plugin's chance to detach from host services and
/// release GPU resources.
/// </para>
/// <para>
/// Like a tool panel, the content is built on first display rather than at restore time, so an
/// inactive document tab costs nothing until the user selects it.
/// </para>
/// </summary>
public sealed class PluginDocumentViewModel : Document, IDisposable
{
    private readonly Func<object> _contentFactory;
    private object? _content;

    public PluginDocumentViewModel(string title, string typeId, object content, bool canClose = true)
        : this(title, typeId, () => content, canClose)
    {
    }

    /// <param name="contentFactory">Builds the plugin's control the first time its tab is shown.</param>
    public PluginDocumentViewModel(string title, string typeId, Func<object> contentFactory, bool canClose = true)
    {
        Title = title;
        Id = typeId;
        CanClose = canClose;
        TypeId = typeId;
        _contentFactory = contentFactory;
    }

    /// <summary>Stable dockable type id, used by the layout serializer to restore this model.</summary>
    public string TypeId { get; }

    /// <summary>The plugin-provided control, created on first use.</summary>
    public object Content => _content ??= _contentFactory();

    public void Dispose()
    {
        // Closing the tab must release the plugin's handlers and GPU resources even while the plugin
        // stays loaded, so disposal is tied to the view model rather than to plugin unload. A tab that
        // was never shown has no content to release.
        if (_content is IDisposable disposable)
            disposable.Dispose();
        _content = null;
    }
}
