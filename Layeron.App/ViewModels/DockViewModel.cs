using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dock.Model.Controls;
using Dock.Model.Core;
using Layeron.App.Layout;
using Layeron.App.Session;
using ReactiveUI;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace Layeron.App.ViewModels;

public class DockViewModel : ViewModelBase
{
    private readonly DockFactory _factory;
    private readonly SessionContext _session;

    private static readonly string LayoutFilePath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Layeron", "layout.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public DockViewModel(SessionContext session)
    {
        _session = session;
        _factory = new DockFactory(session);
        Layout = _factory.BuildOrFallback(null);
        CreateAxialCommand = ReactiveCommand.Create(() => AddSlice(SliceAxis.Axial));
        CreateCoronalCommand = ReactiveCommand.Create(() => AddSlice(SliceAxis.Coronal));
        CreateSagittalCommand = ReactiveCommand.Create(() => AddSlice(SliceAxis.Sagittal));
        CreateCodeEditorCommand = ReactiveCommand.Create(AddCodeEditor);

        ResetLayoutCommand = ReactiveCommand.Create(ResetLayout);

        OpenMaskManagerCommand = ReactiveCommand.Create(OpenMaskManager);
        OpenWindowLevelCommand = ReactiveCommand.Create(OpenWindowLevel);
    }

    public IRootDock? Layout
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>Dockables that could not be restored on the last load.</summary>
    public IReadOnlyList<UnresolvedDockable> Unresolved => _factory.Unresolved;

    /// <summary>True when the last load had unresolvable dockables.</summary>
    public bool HasUnresolved => Unresolved.Count > 0;

    // ── Commands ──────────────────────────────────────────────────────
    
    public ReactiveCommand<RxVoid, RxVoid> CreateAxialCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> CreateCoronalCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> CreateSagittalCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> CreateCodeEditorCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> ResetLayoutCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> OpenMaskManagerCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> OpenWindowLevelCommand { get; }

    // ── Runtime dockable creation ─────────────────────────────────────

    private void AddSlice(SliceAxis axis)
    {
        var name = axis.ToString();
        var newSlice = new SliceViewerViewModel(axis)
        {
            SharedData = _session.SharedData,
            WindowLevel = _session.WindowLevel
        };
        if (Layout is { } root)
            _factory.AddDocument(root, newSlice);
    }

    private void AddCodeEditor()
    {
        var newEditor = new CodeEditorViewModel("Code Editor");

        if (Layout is { } root)
            _factory.AddDocument(root, newEditor);
    }
    
    private void OpenMaskManager()
    {
        if (Layout is { } root)
            _factory.AddTool(root, new MaskManagerViewModel { SharedData = _session.SharedData });
    }
    
    private void OpenWindowLevel()
    {
        if (Layout is { } root)
            _factory.AddTool(root, new WindowLevelViewModel { SharedData = _session.SharedData });
    }

    // ── Layout reset ──────────────────────────────────────────────────

    private void ResetLayout()
    {
        var root = _factory.BuildOrFallback(null);
        Layout = root;
        this.RaisePropertyChanged(nameof(Unresolved));
        this.RaisePropertyChanged(nameof(HasUnresolved));
    }

    // ── Automatic persistence ─────────────────────────────────────────

    /// <summary>Saves synchronously on close so the file is guaranteed written before exit.</summary>
    public void OnWindowClosing()
    {
        if (Layout is not { } root)
            return;

        try
        {
            var dockLayout = _factory.CaptureLayout(root);
            var dir = Path.GetDirectoryName(LayoutFilePath);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(dockLayout, JsonOptions);
            File.WriteAllText(LayoutFilePath, json);
            Console.WriteLine($"[Dock] Layout saved ({json.Length} bytes)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Dock] Failed to save layout: {ex.Message}");
        }
    }

    /// <summary>Restores the persisted layout when it exists; otherwise keeps the default layout already set in the constructor.</summary>
    public async System.Threading.Tasks.Task TryRestoreSavedLayoutAsync()
    {
        if (!File.Exists(LayoutFilePath))
        {
            Console.WriteLine("[Dock] No saved layout file found, keeping default");
            return;
        }

        DockLayout? dockLayout;
        try
        {
            var json = await File.ReadAllTextAsync(LayoutFilePath);
            Console.WriteLine($"[Dock] Loading layout ({json.Length} bytes)");
            dockLayout = JsonSerializer.Deserialize<DockLayout>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Dock] Failed to deserialize layout: {ex.Message}");
            return;
        }

        if (dockLayout == null)
        {
            Console.WriteLine("[Dock] Deserialized layout is null, keeping default");
            return;
        }

        // Use BuildOrFallback so ContextLocator/DockableLocator are set up
        // and the factory is fully initialised, just like construction time.
        Layout = _factory.BuildOrFallback(dockLayout);

        this.RaisePropertyChanged(nameof(Unresolved));
        this.RaisePropertyChanged(nameof(HasUnresolved));
        Console.WriteLine($"[Dock] Layout restored, unresolved={Unresolved.Count}");
    }
}