using Layeron.App.Tools.Geometry;
using System;
using System.Collections.Generic;
using Layeron.App.ViewModels;
using Layeron.Plugin.Abstractions;

namespace Layeron.App.Tools;

/// <summary>
/// Session-scoped tool state: which tool is active, its option values, the routing of pointer
/// and key events from the slice views, and the mask edit history. One instance per session;
/// tool instances are cached per session and discarded with it, so a plugin tool's state never
/// outlives the session (and never keeps a plugin load context alive).
/// </summary>
public sealed class SliceToolService : IDisposable
{
    private readonly ToolCatalog _catalog;
    private readonly SharedDataSourceViewModel _shared;
    private readonly MaskManagerViewModel _masks;
    private readonly RegionAutoConnect _autoConnect;
    private readonly Dictionary<string, ISliceTool> _instances = [];
    private readonly Dictionary<string, ToolOptionValues> _options = [];
    private ISliceTool? _active;
    private SliceToolContext? _context;
    private SliceOverlay? _overlay;
    private MaskWriteMode _writeMode = MaskWriteMode.Set;
    private bool _autoConnectEnabled = true;
    private int _autoConnectMaxGap = 16;
    private bool _disposed;

    public SliceToolService(ToolCatalog catalog, SharedDataSourceViewModel shared, MaskManagerViewModel masks)
    {
        _catalog = catalog ?? new ToolCatalog();
        _shared = shared;
        _masks = masks;
        History = new MaskEditHistory();
        _autoConnect = new RegionAutoConnect(masks);

        _catalog.Changed += OnCatalogChanged;
        _masks.MasksCollectionChanged += OnMasksCollectionChanged;
        History.Changed += OnHistoryChanged;
    }

    /// <summary>Undo/redo stacks for mask edits; see <see cref="MaskEditHistory"/>.</summary>
    internal MaskEditHistory History { get; }

    /// <summary>Session-level cross-slice bridge for the region tools; see <see cref="RegionAutoConnect"/>.</summary>
    internal RegionAutoConnect AutoConnect => _autoConnect;

    /// <summary>Raised when the active tool (or its overlay) changed; slice views rebind cursors and gestures.</summary>
    public event Action? ActiveToolChanged;

    /// <summary>Raised when the tool preview overlay changed.</summary>
    public event Action? OverlayChanged;

    /// <summary>Raised when undo/redo availability changed.</summary>
    public event Action? HistoryChanged;

    /// <summary>Raised when the shared write mode or an option value changed.</summary>
    public event Action? StateChanged;

    public string? ActiveToolId => _active?.Descriptor.Id;

    public SliceOverlay? Overlay => _overlay;

    /// <summary>True while a tool is active and slice views must route left-button gestures to it.</summary>
    public bool HasActiveTool => _active != null;

    /// <summary>The shared add/erase switch; region tools apply it to their writes.</summary>
    public MaskWriteMode WriteMode
    {
        get => _writeMode;
        set
        {
            if (_writeMode == value) return;
            _writeMode = value;
            StateChanged?.Invoke();
        }
    }

    /// <summary>
    /// Cross-slice auto-connection switch (toolbox-level state, default on): two consecutive
    /// single-layer region commits on the same layer, plane and mode bridge their intermediate
    /// slices through shape interpolation.
    /// </summary>
    public bool AutoConnectEnabled
    {
        get => _autoConnectEnabled;
        set
        {
            if (_autoConnectEnabled == value) return;
            _autoConnectEnabled = value;
            StateChanged?.Invoke();
        }
    }

    /// <summary>
    /// Maximum slice gap for auto-connection; two commits further apart start a new chain instead
    /// of bridging. Clamped to 2..1024 - a gap below 2 has no intermediate slices to fill.
    /// </summary>
    public int AutoConnectMaxGap
    {
        get => _autoConnectMaxGap;
        set
        {
            var clamped = Math.Clamp(value, 2, 1024);
            if (_autoConnectMaxGap == clamped) return;
            _autoConnectMaxGap = clamped;
            StateChanged?.Invoke();
        }
    }

    public bool CanUndo => History.CanUndo;
    public bool CanRedo => History.CanRedo;

    /// <summary>One-line hint for the toolbox status area.</summary>
    public string StatusText
    {
        get
        {
            if (_active == null)
                return "浏览模式：左键拖动平移，右键移动十字线";
            if (_shared.Array == null)
                return "请先加载体数据";
            if (_masks.ActiveMask == null)
                return "请先添加或选择一个掩膜层";
            return _active.Descriptor.Description ?? _active.Descriptor.DisplayName;
        }
    }

    /// <summary>Activates the tool with the given id; null deactivates (browse mode).</summary>
    public void ActivateTool(string? toolId)
    {
        if (_disposed || string.Equals(ActiveToolId, toolId, StringComparison.Ordinal))
            return;

        DeactivateCore();

        if (toolId == null)
        {
            ActiveToolChanged?.Invoke();
            StateChanged?.Invoke();
            return;
        }

        var contribution = _catalog.Find(toolId);
        if (contribution == null)
        {
            Layeron.App.Logging.Log.Warn("Tools", "Unknown tool id {ToolId}", toolId);
            return;
        }

        if (!_instances.TryGetValue(toolId, out var tool))
        {
            tool = contribution.Factory();
            _instances[toolId] = tool;
        }

        _active = tool;
        _context = new SliceToolContext(this, _shared, _masks, toolId);
        tool.Activate(_context);

        Layeron.App.Logging.Log.Debug("Tools", "Tool {ToolId} activated", toolId);
        ActiveToolChanged?.Invoke();
        StateChanged?.Invoke();
    }

    public void DeactivateTool() => ActivateTool(null);

    /// <summary>Routes one pointer event from a slice view to the active tool.</summary>
    public void HandlePointer(SlicePlane plane, int index, double u, double v, SlicePointerPhase phase, SliceKeyModifiers modifiers, int clickCount)
    {
        if (_disposed || _active == null)
            return;

        var e = new SlicePointerEvent(plane, index, u, v, phase, modifiers, clickCount);
        _active.HandlePointer(in e);
    }

    /// <summary>Routes Enter/Escape from a slice view to the active tool.</summary>
    public void HandleKey(SliceToolKey key)
    {
        if (!_disposed)
            _active?.HandleKey(key);
    }

    public void Undo() => History.Undo(_masks);

    public void Redo() => History.Redo(_masks);

    /// <summary>Stores the active tool's preview overlay; slice views render it on the matching slice.</summary>
    public void SetOverlay(SliceOverlay? overlay)
    {
        _overlay = overlay;
        OverlayChanged?.Invoke();
    }

    // ── Option values ─────────────────────────────────────────────────

    public double GetNumber(string toolId, string optionId) =>
        ValuesFor(toolId)?.GetNumber(optionId, FindDescriptor(toolId)) ?? 0;

    public bool GetToggle(string toolId, string optionId) =>
        ValuesFor(toolId)?.GetToggle(optionId, FindDescriptor(toolId)) ?? false;

    public int GetChoice(string toolId, string optionId) =>
        ValuesFor(toolId)?.GetChoice(optionId, FindDescriptor(toolId)) ?? 0;

    public void SetNumber(string toolId, string optionId, double value)
    {
        ValuesFor(toolId)?.SetNumber(optionId, value);
        StateChanged?.Invoke();
    }

    public void SetToggle(string toolId, string optionId, bool value)
    {
        ValuesFor(toolId)?.SetToggle(optionId, value);
        StateChanged?.Invoke();
    }

    public void SetChoice(string toolId, string optionId, int value)
    {
        ValuesFor(toolId)?.SetChoice(optionId, value);
        StateChanged?.Invoke();
    }

    private ToolOptionValues? ValuesFor(string toolId)
    {
        if (_disposed) return null;
        if (!_options.TryGetValue(toolId, out var values))
        {
            values = new ToolOptionValues();
            _options[toolId] = values;
        }

        return values;
    }

    private SliceToolDescriptor? FindDescriptor(string toolId) => _catalog.Find(toolId)?.Descriptor;

    // ── Lifecycle ─────────────────────────────────────────────────────

    private void DeactivateCore()
    {
        if (_active == null) return;
        try
        {
            _active.Deactivate();
        }
        catch (Exception ex)
        {
            Layeron.App.Logging.Log.Error("Tools", ex, "Tool {ToolId} failed to deactivate", ActiveToolId);
        }

        _active = null;
        _context = null;
        SetOverlay(null);
    }

    private void OnCatalogChanged()
    {
        // A plugin tool disappeared (unload/hot reload): drop its cached instance so the plugin
        // load context is not kept alive, and deactivate it if it was active.
        var removed = new List<string>();
        foreach (var id in _instances.Keys)
        {
            if (_catalog.Find(id) == null)
                removed.Add(id);
        }

        var wasActiveRemoved = removed.Contains(ActiveToolId ?? string.Empty);
        if (wasActiveRemoved)
            DeactivateCore();

        foreach (var id in removed)
            _instances.Remove(id);

        if (removed.Count > 0)
        {
            Layeron.App.Logging.Log.Debug("Tools", "Dropped {Count} removed tool(s)", removed.Count);
            ActiveToolChanged?.Invoke();
            StateChanged?.Invoke();
        }
    }

    private void OnMasksCollectionChanged()
    {
        // Layers that were removed take their commands with them: undoing into a dead layer
        // would either throw or silently write into a detached bitmap.
        History.Prune(layer => _masks.Masks.Contains(layer));
    }

    private void OnHistoryChanged() => HistoryChanged?.Invoke();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        DeactivateCore();
        _instances.Clear();
        _options.Clear();
        History.Clear();
        _catalog.Changed -= OnCatalogChanged;
        _masks.MasksCollectionChanged -= OnMasksCollectionChanged;
        History.Changed -= OnHistoryChanged;
    }
}
