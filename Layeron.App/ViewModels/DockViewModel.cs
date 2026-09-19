using System.Windows.Input;
using Dock.Model.Controls;
using Dock.Model.Core;
using ReactiveUI;

namespace Layeron.App.ViewModels;

public class DockViewModel : ViewModelBase
{
    private int _documentCount;
    private int _toolCount;
    private int _sliceCount;
    private int _codeEditorCount;
    private readonly SharedDataSourceViewModel _sharedData;
    private readonly DockFactory _factory;

    public DockViewModel(SharedDataSourceViewModel sharedData, MaskManagerViewModel maskManager)
    {
        _sharedData = sharedData;
        _factory = new DockFactory(sharedData, maskManager);
        Layout = _factory.CreateLayout();
        _factory.InitLayout(Layout);
        
        AddDocumentCommand = ReactiveCommand.Create(AddDocument);
        AddToolCommand = ReactiveCommand.Create(AddTool);
        CreateAxialCommand = ReactiveCommand.Create(() => AddSlice(SliceAxis.Axial));
        CreateCoronalCommand = ReactiveCommand.Create(() => AddSlice(SliceAxis.Coronal));
        CreateSagittalCommand = ReactiveCommand.Create(() => AddSlice(SliceAxis.Sagittal));
        CreateCodeEditorCommand = ReactiveCommand.Create(AddCodeEditor);
    }

    public IRootDock? Layout
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public ICommand AddDocumentCommand { get; }
    public ICommand AddToolCommand { get; }
    public ICommand CreateAxialCommand { get; }
    public ICommand CreateCoronalCommand { get; }
    public ICommand CreateSagittalCommand { get; }
    public ICommand CreateCodeEditorCommand { get; }

    private void AddDocument()
    {
        _documentCount++;
        var newDocument = new DocumentWindowViewModel 
        { 
            Title = $"Document {_documentCount}", 
            Id = $"Document{_documentCount}",
            Content = $"This is document {_documentCount}. You can add any content here, such as text, code, or UI elements.",
        };

        AddToMainLayout(newDocument);
    }

    private void AddTool()
    {
        _toolCount++;
        var newTool = new ToolWindowViewModel 
        { 
            Title = $"Tool {_toolCount}", 
            Id = $"Tool{_toolCount}",
            Content = $"This is tool {_toolCount}. Tools are typically used for utilities, properties, output, or other auxiliary functions.",
        };

        if (Layout is not { } rootDock) return;
        _factory.AddTool(rootDock, newTool);
    }

    private void AddSlice(SliceAxis axis)
    {
        _sliceCount++;
        var name = axis.ToString();
        var newSlice = new SliceViewerViewModel($"{name} Slice {_sliceCount}", $"Slice{name}{_sliceCount}", axis)
        {
            SharedData = _sharedData
        };

        AddToMainLayout(newSlice);
    }

    private void AddCodeEditor()
    {
        _codeEditorCount++;
        var newEditor = new CodeEditorViewModel($"Code Editor {_codeEditorCount}", $"CodeEditor{_codeEditorCount}");

        AddToMainLayout(newEditor);
    }

    private void AddToMainLayout(IDockable newDockable)
    {
        if (Layout is not { } rootDock) return;
        _factory.AddDocument(rootDock, newDockable);
    }
}
