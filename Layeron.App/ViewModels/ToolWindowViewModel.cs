using Dock.Model.ReactiveUI.Controls;

namespace Layeron.App.ViewModels;

public class ToolWindowViewModel : Tool
{
    public ToolWindowViewModel()
    {
        Title = "Tool Window";
        Id = "ToolWindow";
        CanClose = true;
        Content = "This is a tool window with some sample content. You can add controls, text, or other UI elements here.";
    }
    
    public string Content { get; set; }
}