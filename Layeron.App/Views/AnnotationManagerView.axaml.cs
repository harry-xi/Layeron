using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Layeron.App.Views;

public partial class AnnotationManagerView : UserControl
{
    public AnnotationManagerView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
