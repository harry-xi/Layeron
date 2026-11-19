using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Disposables;
using Dock.Model.ReactiveUI.Controls;
using Layeron.App.Layout;
using Layeron.App.Session;
using Layeron.App.Tools;
using Layeron.Plugin.Abstractions;
using ReactiveUI;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace Layeron.App.ViewModels;

/// <summary>
/// The toolbox panel: one button per tool (built-ins plus plugin contributions), a generic
/// options editor for the active tool, the shared add/erase switch and the undo/redo entry
/// points. The panel owns no tool logic - it adapts the session's <see cref="SliceToolService"/>
/// and the <see cref="ToolCatalog"/> the same way the filter runner adapts its catalog.
/// </summary>
public sealed class ToolboxViewModel : Tool, IDockableIdentity, IDisposable
{
    private readonly ISessionManager _sessions;
    private readonly ToolCatalog _catalog;
    private readonly IDisposable _sessionSubscription;
    private SliceToolService? _service;
    private bool _disposed;
    private bool _eraseMode;

    public ToolboxViewModel(ISessionManager sessions, ToolCatalog catalog)
    {
        _sessions = sessions;
        _catalog = catalog ?? new ToolCatalog();
        Title = "工具箱";
        Id = DockableIds.Toolbox;
        CanClose = true;
        CanDockAsDocument = false;

        SelectToolCommand = ReactiveCommand.Create<string?>(SelectTool);
        UndoCommand = ReactiveCommand.Create(Undo);
        RedoCommand = ReactiveCommand.Create(Redo);

        _catalog.Changed += OnCatalogChanged;
        RebuildTools();

        _sessionSubscription = _sessions.CurrentSession.Subscribe(AttachSession);
    }

    public string TypeId => DockableIds.Toolbox;

    /// <summary>One entry per tool; selecting one activates it.</summary>
    public ObservableCollection<ToolboxToolViewModel> Tools { get; } = [];

    /// <summary>Option editors for the active tool; empty in browse mode.</summary>
    public ObservableCollection<ToolboxOptionViewModel> Options { get; } = [];

    /// <summary>Shared add/erase switch for the region tools.</summary>
    public bool IsEraseMode
    {
        get => _eraseMode;
        set
        {
            if (_eraseMode == value) return;
            _eraseMode = value;
            _service?.WriteMode = value ? MaskWriteMode.Clear : MaskWriteMode.Set;
            this.RaisePropertyChanged();
        }
    }

    public bool CanUndo => _service?.CanUndo ?? false;

    public bool CanRedo => _service?.CanRedo ?? false;

    public string? StatusText => _service?.StatusText;

    public ReactiveCommand<string?, RxVoid> SelectToolCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> UndoCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> RedoCommand { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _catalog.Changed -= OnCatalogChanged;
        _sessionSubscription.Dispose();
        DetachService();
    }

    // ── Session binding ───────────────────────────────────────────

    private void AttachSession(ISession? session)
    {
        DetachService();
        _service = session?.ToolService;
        if (_service != null)
        {
            _service.ActiveToolChanged += OnServiceStateChanged;
            _service.StateChanged += OnServiceStateChanged;
            _service.HistoryChanged += OnServiceStateChanged;
        }

        _eraseMode = _service?.WriteMode == MaskWriteMode.Clear;
        this.RaisePropertyChanged(nameof(IsEraseMode));
        SyncFromService();
    }

    private void DetachService()
    {
        if (_service != null)
        {
            _service.ActiveToolChanged -= OnServiceStateChanged;
            _service.StateChanged -= OnServiceStateChanged;
            _service.HistoryChanged -= OnServiceStateChanged;
            _service = null;
        }
    }

    private void OnServiceStateChanged() => SyncFromService();

    private void SyncFromService()
    {
        var activeId = _service?.ActiveToolId;
        foreach (var tool in Tools)
            tool.IsSelected = tool.Id == activeId;
        RebuildOptions();
        this.RaisePropertyChanged(nameof(CanUndo));
        this.RaisePropertyChanged(nameof(CanRedo));
        this.RaisePropertyChanged(nameof(StatusText));
    }

    // ── Tool list ─────────────────────────────────────────────────

    private void OnCatalogChanged() => RebuildTools();

    private void RebuildTools()
    {
        // The browse entry (null id) sits above every contributed tool; contributed tools
        // keep the catalog's order (built-ins by group/order, then plugins by registration).
        var activeId = _service?.ActiveToolId;
        Tools.Clear();
        Tools.Add(new ToolboxToolViewModel(null, "浏览", activeId == null));
        foreach (var contribution in _catalog.Items)
        {
            var descriptor = contribution.Descriptor;
            Tools.Add(new ToolboxToolViewModel(descriptor.Id, descriptor.DisplayName, descriptor.Id == activeId));
        }
    }

    private void SelectTool(string? toolId) => _service?.ActivateTool(toolId);

    private void Undo() => _service?.Undo();

    private void Redo() => _service?.Redo();

    private void RebuildOptions()
    {
        Options.Clear();
        var service = _service;
        if (service == null)
            return;

        var activeId = service.ActiveToolId;
        if (activeId == null)
            return;

        var descriptor = _catalog.Find(activeId)?.Descriptor;
        if (descriptor?.Options == null)
            return;

        foreach (var option in descriptor.Options)
            Options.Add(new ToolboxOptionViewModel(service, activeId, option));
    }
}

/// <summary>One toolbox button: the tool id (null = browse mode), its name and selection state.</summary>
public sealed class ToolboxToolViewModel : ViewModelBase
{
    public ToolboxToolViewModel(string? id, string name, bool isSelected)
    {
        Id = id;
        Name = name;
        _isSelected = isSelected;
    }

    public string? Id { get; }

    public string Name { get; }

    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }
}

/// <summary>
/// Generic editor for one <see cref="SliceToolOption"/> of the active tool: a numeric up/down
/// for Number, a toggle for Toggle, and a combo for Choice, all writing straight into the
/// session's option values.
/// </summary>
public sealed class ToolboxOptionViewModel : ViewModelBase
{
    private readonly SliceToolService _service;
    private readonly string _toolId;
    private double _numberValue;
    private bool _toggleValue;
    private int _choiceValue;

    public ToolboxOptionViewModel(SliceToolService service, string toolId, SliceToolOption option)
    {
        _service = service;
        _toolId = toolId;
        Option = option;
        _numberValue = service.GetNumber(toolId, option.Id);
        _toggleValue = service.GetToggle(toolId, option.Id);
        _choiceValue = service.GetChoice(toolId, option.Id);
    }

    public SliceToolOption Option { get; }

    public string Label => Option.Label;

    public bool IsNumber => Option.Kind == SliceToolOptionKind.Number;

    public bool IsToggle => Option.Kind == SliceToolOptionKind.Toggle;

    public bool IsChoice => Option.Kind == SliceToolOptionKind.Choice;

    public double NumberValue
    {
        get => _numberValue;
        set
        {
            // NumericUpDown clamps in its UI, but the service is the authority anyway.
            var clamped = Math.Clamp(value, Option.Min, Option.Max);
            if (Math.Abs(_numberValue - clamped) < double.Epsilon) return;
            _service.SetNumber(_toolId, Option.Id, clamped);
            this.RaiseAndSetIfChanged(ref _numberValue, clamped);
        }
    }

    public bool ToggleValue
    {
        get => _toggleValue;
        set
        {
            if (_toggleValue == value) return;
            _service.SetToggle(_toolId, Option.Id, value);
            this.RaiseAndSetIfChanged(ref _toggleValue, value);
        }
    }

    public int ChoiceValue
    {
        get => _choiceValue;
        set
        {
            if (_choiceValue == value) return;
            _service.SetChoice(_toolId, Option.Id, value);
            this.RaiseAndSetIfChanged(ref _choiceValue, value);
        }
    }

    public System.Collections.Generic.IReadOnlyList<string> Choices => Option.Choices ?? [];
}


