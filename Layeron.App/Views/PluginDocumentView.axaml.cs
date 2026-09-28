using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Layeron.App.Views;

/// <summary>
/// Hosts a control produced by a plugin for its document tab. The host holds the content as
/// <see cref="object"/> and never names a plugin type, so nothing in the application references
/// plugin code.
/// </summary>
public partial class PluginDocumentView : UserControl
{
    public PluginDocumentView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
