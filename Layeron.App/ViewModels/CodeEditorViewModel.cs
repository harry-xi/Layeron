using AvaloniaEdit.Document;
using Dock.Model.ReactiveUI.Controls;
using Layeron.App.Layout;

namespace Layeron.App.ViewModels;

public class CodeEditorViewModel : Document, IDescribedDockable, IDockableIdentity
{
    public CodeEditorViewModel(string title)
    {
        Title = title;
        CanClose = true;
        Document = new TextDocument(InitialSnippet);
    }

    private const string InitialSnippet = """
        using System;

        public static class Program
        {
            public static void Main()
            {
                Console.WriteLine("Hello, Layeron!");
            }
        }
        """;

    public TextDocument Document { get; }

    public string Text
    {
        get => Document.Text;
        set => Document.Text = value;
    }

    /// <summary>This pane's registered id; the same value <c>DockFactory</c> registers the factory under.</summary>
    public string TypeId => DockableIds.CodeEditor;

    /// <summary>
    /// Round-trip identity: the factory sets this when the panel is built, so a save keeps the title the
    /// user is actually looking at rather than reconstructing it from the CLR type.
    /// </summary>
    public DockableDescriptor? Descriptor { get; set; }
}