using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dock.Model.Controls;
using Dock.Model.Core;
using Layeron.App.Layout;
using Layeron.App.Session;
using Layeron.Plugin.Abstractions;
using ReactiveUI;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace Layeron.App.ViewModels;

public class DockViewModel : ViewModelBase
{
    private readonly ISessionManager _sessionManager;

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

    public DockViewModel(ISessionManager sessions, IPreviewChannel? preview = null)
    {
        _sessionManager = sessions;
        Factory = new DockFactory(sessions, preview);
        Layout = Factory.BuildOrFallback(null);
        CreateAxialCommand = ReactiveCommand.Create(() => AddSlice(SliceAxis.Axial));
        CreateCoronalCommand = ReactiveCommand.Create(() => AddSlice(SliceAxis.Coronal));
        CreateSagittalCommand = ReactiveCommand.Create(() => AddSlice(SliceAxis.Sagittal));
        CreateCodeEditorCommand = ReactiveCommand.Create(AddCodeEditor);

        ResetLayoutCommand = ReactiveCommand.Create(ResetLayout);

        OpenMaskManagerCommand = ReactiveCommand.Create(OpenMaskManager);
        OpenWindowLevelCommand = ReactiveCommand.Create(OpenWindowLevel);
    }

    /// <summary>
    /// The dock factory. Exposed so the plugin host can register and unregister tool factories: every
    /// dockable type - built in or contributed - is wired up in exactly one place.
    /// </summary>
    public DockFactory Factory { get; }

    public IRootDock? Layout
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>Dockables that could not be restored on the last load.</summary>
    public IReadOnlyList<UnresolvedDockable> Unresolved => Factory.Unresolved;

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
        if (Layout is not { } root)
            return;
        if (Factory.CreateDockable(DockableIds.ForSliceAxis(axis)) is { } slice)
            Factory.AddDocument(root, slice);
    }

    private void AddCodeEditor()
    {
        var newEditor = new CodeEditorViewModel("Code Editor");

        if (Layout is { } root)
            Factory.AddDocument(root, newEditor);
    }

    /// <summary>
    /// Opens a dockable by type id. An instance of the same type that is already docked is brought to
    /// the front instead of opening a second copy, which is what a menu entry is expected to do. Used by
    /// the plugin host for contributed tools and safe to call for any registered dockable.
    /// </summary>
    public bool OpenToolById(string typeId, string? title = null)
    {
        if (Layout is not { } root)
            return false;

        if (Factory.FindDockable(root, d => d.Id == typeId) is { } existing)
        {
            Factory.SetActiveDockable(existing);
            Factory.SetFocusedDockable(existing.Owner as IDock ?? root, existing);
            return true;
        }

        if (Factory.CreateDockable(typeId, title) is not { } tool)
            return false;

        Factory.AddTool(root, tool);
        return true;
    }
    
    private void OpenMaskManager()
        => OpenSessionTool(DockableIds.MaskManager, _sessionManager.Current?.MaskManager);
    
    private void OpenWindowLevel()
        => OpenSessionTool(DockableIds.WindowLevel, _sessionManager.Current?.WindowLevel);

    /// <summary>Opens a session-owned tool unless the same instance is already docked.</summary>
    private void OpenSessionTool(string typeId, IDockable? tool)
    {
        if (Layout is not { } root || tool == null)
            return;
        if (Factory.FindDockable(root, d => ReferenceEquals(d, tool)) != null)
            return; // already open
        Factory.AddTool(root, tool);
    }

    // ── Layout reset ──────────────────────────────────────────────────

    private void ResetLayout()
    {
        if (Layout is { } old)
            Factory.DisposeTree(old);
        var root = Factory.BuildOrFallback(null);
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
            var dockLayout = Factory.CaptureLayout(root);
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

        if (Layout is { } old)
            Factory.DisposeTree(old);

        // Use BuildOrFallback so ContextLocator/DockableLocator are set up
        // and the factory is fully initialised, just like construction time.
        Layout = Factory.BuildOrFallback(dockLayout);

        this.RaisePropertyChanged(nameof(Unresolved));
        this.RaisePropertyChanged(nameof(HasUnresolved));
        Console.WriteLine($"[Dock] Layout restored, unresolved={Unresolved.Count}");
    }
}
