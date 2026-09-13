using Avalonia;
using Avalonia.Controls;
using AvaloniaEdit.Highlighting;

namespace Layeron.App.Views;

public partial class CodeEditorView : UserControl
{
    public CodeEditorView()
    {
        InitializeComponent();
        var definition = HighlightingManager.Instance.GetDefinition("C#");
        if (definition != null)
        {
            Editor.SyntaxHighlighting = definition;
        }
    }
}