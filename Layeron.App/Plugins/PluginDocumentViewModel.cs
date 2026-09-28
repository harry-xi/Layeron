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
/// </summary>
public sealed class PluginDocumentViewModel : Document, IDisposable
{
    public PluginDocumentViewModel(string title, string typeId, object content, bool canClose = true)
    {
        Title = title;
        Id = typeId;
        CanClose = canClose;
        Content = content;
        TypeId = typeId;
    }

    /// <summary>Stable dockable type id, used by the layout serializer to restore this tab.</summary>
    public string TypeId { get; }

    /// <summary>The plugin-provided control.</summary>
    public object Content { get; }

    public void Dispose()
    {
        // Closing the tab must release the plugin's handlers and GPU resources even while the plugin
        // stays loaded, so disposal is tied to the view model rather than to plugin unload.
        if (Content is IDisposable disposable)
            disposable.Dispose();
    }
}
