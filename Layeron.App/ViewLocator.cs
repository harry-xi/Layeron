using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Dock.Model.Core;
using Layeron.App.ViewModels;
using Layeron.App.Plugins;

namespace Layeron.App;

/// <summary>
/// Given a view model, returns the corresponding view if possible.
/// </summary>
[RequiresUnreferencedCode(
    "Default implementation of ViewLocator involves reflection which may be trimmed away.",
    Url = "https://docs.avaloniaui.net/docs/concepts/view-locator")]
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        // Dock builds plugin panels through the DockControl, not through this locator, because
        // PluginToolViewModel is not a ViewModelBase. Returning the plugin's own control here covers the
        // paths that do come through the locator, and an opaque payload is shown as text rather than
        // silently rendering nothing.
        if (param is PluginToolViewModel plugin)
        {
            return plugin.Content as Control
                ?? new TextBlock { Text = plugin.Content.ToString() ?? "" };
        }

        if (param is PluginDocumentViewModel document)
        {
            return document.Content as Control
                ?? new TextBlock { Text = document.Content.ToString() ?? "" };
        }

        var name = param.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        var type = Type.GetType(name);

        if (type != null)
        {
            return (Control)Activator.CreateInstance(type)!;
        }

        return new TextBlock { Text = "Not Found: " + name };
    }

    public bool Match(object? data)
    {
        return data is ViewModelBase || data is IDockable;
    }
}
