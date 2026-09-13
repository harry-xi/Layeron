using AvaloniaEdit.Document;
using Dock.Model.ReactiveUI.Controls;

namespace Layeron.App.ViewModels;

public class CodeEditorViewModel : Document
{
    public CodeEditorViewModel(string title, string id)
    {
        Title = title;
        Id = id;
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
}