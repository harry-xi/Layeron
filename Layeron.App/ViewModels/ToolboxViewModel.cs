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
    private AnnotationManagerViewModel? _annotations;
    private bool _disposed;
    private bool _eraseMode;
    private bool _autoConnect = true;
    private double _autoConnectGap = 16;

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

    /// <summary>Tool buttons grouped by the descriptor's group key; selecting one activates it.</summary>
    public ObservableCollection<ToolboxToolGroupViewModel> ToolGroups { get; } = [];

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

    /// <summary>
    /// Cross-slice auto-connection switch: with it on, two consecutive single-layer region commits on
    /// the same layer, plane and mode fill their intermediate slices through shape interpolation.
    /// </summary>
    public bool AutoConnectEnabled
    {
        get => _autoConnect;
        set
        {
            if (_autoConnect == value) return;
            _autoConnect = value;
            if (_service != null)
                _service.AutoConnectEnabled = value;
            this.RaisePropertyChanged();
        }
    }

    /// <summary>Maximum slice gap for auto-connection; commits further apart start a new chain.</summary>
    public double AutoConnectMaxGap
    {
        get => _autoConnectGap;
        set
        {
            if (Math.Abs(_autoConnectGap - value) < 0.5) return;
            _autoConnectGap = value;
            if (_service != null)
                _service.AutoConnectMaxGap = (int)Math.Round(value);
            this.RaisePropertyChanged();
        }
    }

    public bool CanUndo => _service?.CanUndo ?? false;

    public bool CanRedo => _service?.CanRedo ?? false;

    public string? StatusText => _service?.StatusText;

    /// <summary>True while the session's annotation manager has an active drawing tool.</summary>
    public bool IsAnnotationMode => _annotations is { ActiveTool: not AnnotationTool.None };

    /// <summary>Annotation drawing wins the left button on slices; surface the clash instead of hiding it.</summary>
    public bool ShowAnnotationConflict => IsAnnotationMode && _service?.ActiveToolId != null;

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

        _annotations = session?.AnnotationManager;
        if (_annotations != null)
            _annotations.PropertyChanged += OnAnnotationsPropertyChanged;

        _eraseMode = _service?.WriteMode == MaskWriteMode.Clear;
        _autoConnect = _service?.AutoConnectEnabled ?? true;
        _autoConnectGap = _service?.AutoConnectMaxGap ?? 16;
        this.RaisePropertyChanged(nameof(IsEraseMode));
        this.RaisePropertyChanged(nameof(AutoConnectEnabled));
        this.RaisePropertyChanged(nameof(AutoConnectMaxGap));
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

        if (_annotations != null)
        {
            _annotations.PropertyChanged -= OnAnnotationsPropertyChanged;
            _annotations = null;
        }
    }

    private void OnAnnotationsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AnnotationManagerViewModel.ActiveTool))
        {
            this.RaisePropertyChanged(nameof(IsAnnotationMode));
            this.RaisePropertyChanged(nameof(ShowAnnotationConflict));
        }
    }

    private void OnServiceStateChanged() => SyncFromService();

    private void SyncFromService()
    {
        var activeId = _service?.ActiveToolId;
        foreach (var group in ToolGroups)
            foreach (var tool in group.Tools)
                tool.IsSelected = tool.Id == activeId;

        RebuildOptions();
        this.RaisePropertyChanged(nameof(CanUndo));
        this.RaisePropertyChanged(nameof(CanRedo));
        this.RaisePropertyChanged(nameof(StatusText));
        this.RaisePropertyChanged(nameof(ShowAnnotationConflict));
    }

    // ── Tool list ─────────────────────────────────────────────────

    private void OnCatalogChanged() => RebuildTools();

    private void RebuildTools()
    {
        // The browse entry (null id) sits above every contributed tool; contributed tools are
        // grouped by their descriptor key, ordered within the group and by the group's rank.
        var activeId = _service?.ActiveToolId;
        ToolGroups.Clear();

        var browse = new ToolboxToolGroupViewModel(null, SelectToolCommand);
        browse.Tools.Add(new ToolboxToolViewModel(null, "浏览", activeId == null));
        ToolGroups.Add(browse);

        foreach (var group in _catalog.Items
                     .GroupBy(c => c.Descriptor.Group ?? string.Empty)
                     .OrderBy(g => GroupRank(g.Key))
                     .ThenBy(g => g.Key))
        {
            var groupVm = new ToolboxToolGroupViewModel(GroupHeader(group.Key), SelectToolCommand);
            foreach (var contribution in group.OrderBy(c => c.Descriptor.Order))
            {
                var descriptor = contribution.Descriptor;
                groupVm.Tools.Add(new ToolboxToolViewModel(descriptor.Id, descriptor.DisplayName, descriptor.Id == activeId));
            }

            ToolGroups.Add(groupVm);
        }
    }

    /// <summary>Stable ordering of known groups; unknown (plugin) groups sort after the built-ins.</summary>
    private static int GroupRank(string group) => group switch
    {
        "region" => 0,
        "retouch" => 1,
        _ => 2
    };

    private static string GroupHeader(string group) => group switch
    {
        "region" => "区域",
        "retouch" => "修边",
        "" => "工具",
        _ => group
    };

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

/// <summary>One toolbox group: a header (null for the browse entry) and its tool buttons.</summary>
public sealed class ToolboxToolGroupViewModel
{
    public ToolboxToolGroupViewModel(string? header, ReactiveCommand<string?, RxVoid> selectToolCommand)
    {
        Header = header;
        SelectToolCommand = selectToolCommand;
    }

    public string? Header { get; }

    /// <summary>False for the browse entry, which renders without a section title.</summary>
    public bool HasHeader => !string.IsNullOrEmpty(Header);

    public ReactiveCommand<string?, RxVoid> SelectToolCommand { get; }

    public ObservableCollection<ToolboxToolViewModel> Tools { get; } = [];
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


