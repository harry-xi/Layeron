using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Layeron.App.Views;

public partial class MaskManagerView : UserControl
{
    public MaskManagerView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}