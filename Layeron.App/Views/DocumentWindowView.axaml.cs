using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Layeron.App.Views;

public partial class DocumentWindowView : UserControl
{
    public DocumentWindowView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}