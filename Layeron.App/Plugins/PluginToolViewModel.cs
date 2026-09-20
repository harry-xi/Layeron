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
/// </summary>
public sealed class PluginToolViewModel : Tool, IDisposable
{
    public PluginToolViewModel(string title, string typeId, object content, bool canClose = true)
    {
        Title = title;
        Id = typeId;
        CanClose = canClose;
        Content = content;
        TypeId = typeId;
    }

    /// <summary>Stable dockable type id, used by the layout serializer to restore this panel.</summary>
    public string TypeId { get; }

    /// <summary>The plugin-provided control.</summary>
    public object Content { get; }

    public void Dispose()
    {
        // Closing the panel must release the plugin's handlers even while the plugin stays loaded.
        if (Content is IDisposable disposable)
            disposable.Dispose();
    }
}
