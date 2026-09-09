using Dock.Model.ReactiveUI.Controls;

namespace Layeron.App.ViewModels;

public class DocumentWindowViewModel : Document
{
    public DocumentWindowViewModel()
    {
        Title = "Document";
        Id = "Document";
        CanClose = true;
        Content = "This is a document window. You can edit documents, view files, or display any content here.";
    }
    
    public string Content { get; set; }
}