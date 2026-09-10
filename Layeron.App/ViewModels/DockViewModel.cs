using System.Windows.Input;
using Dock.Model.Controls;
using Dock.Model.Core;
using ReactiveUI;

namespace Layeron.App.ViewModels;

public class DockViewModel : ViewModelBase
{
    private int _documentCount;
    private int _toolCount;
    private readonly SharedDataSourceViewModel _sharedData;

    public DockViewModel(SharedDataSourceViewModel sharedData, MaskManagerViewModel maskManager)
    {
        _sharedData = sharedData;
        var factory = new DockFactory(sharedData, maskManager);
        Layout = factory.CreateLayout();
        factory.InitLayout(Layout);
        
        AddDocumentCommand = ReactiveCommand.Create(AddDocument);
        AddToolCommand = ReactiveCommand.Create(AddTool);
    }

    public IRootDock? Layout
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public ICommand AddDocumentCommand { get; }
    public ICommand AddToolCommand { get; }

    private void AddDocument()
    {
        if (Layout is not { } rootDock) return;
        
        _documentCount++;
        var newDocument = new DocumentWindowViewModel 
        { 
            Title = $"Document {_documentCount}", 
            Id = $"Document{_documentCount}",
            Content = $"This is document {_documentCount}. You can add any content here, such as text, code, or UI elements.",
        };

        if (rootDock.DefaultDockable is IProportionalDock mainLayout)
        {
            foreach (var dockable in mainLayout.VisibleDockables ?? new System.Collections.ObjectModel.ObservableCollection<IDockable>())
            {
                if (dockable is IDocumentDock documentDock)
                {
                    documentDock.VisibleDockables?.Add(newDocument);
                    documentDock.ActiveDockable = newDocument;
                    break;
                }
            }
        }
    }

    private void AddTool()
    {
        if (Layout is not { } rootDock) return;
        
        _toolCount++;
        var newTool = new ToolWindowViewModel 
        { 
            Title = $"Tool {_toolCount}", 
            Id = $"Tool{_toolCount}",
            Content = $"This is tool {_toolCount}. Tools are typically used for utilities, properties, output, or other auxiliary functions.",
        };

        if (rootDock.DefaultDockable is IProportionalDock mainLayout)
        {
            foreach (var dockable in mainLayout.VisibleDockables ?? new System.Collections.ObjectModel.ObservableCollection<IDockable>())
            {
                if (dockable is IToolDock toolDock)
                {
                    toolDock.VisibleDockables?.Add(newTool);
                    toolDock.ActiveDockable = newTool;
                    break;
                }
            }
        }
    }
}
