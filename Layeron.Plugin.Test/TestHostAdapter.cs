using System.Collections.Concurrent;
using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;
using Layeron.Plugin.Host;

namespace Layeron.Plugin.Test;

/// <summary>
/// Headless host adapter used by the tests: it implements the same contract the Avalonia app
/// implements, which is what makes the plugin system testable without a UI. If the contract were
/// UI-shaped, every lifecycle test would need a window.
/// </summary>
internal sealed class TestHostAdapter : IPluginHostAdapter
{
    public TestHostAdapter(Big3DArray<byte>? volume = null)
    {
        Services = new TestServices(volume);
        Log = new CollectingLog();
    }

    public IPluginServices Services { get; }

    public CollectingLog Log { get; }

    IPluginLog IPluginHostAdapter.Log => Log;

    /// <summary>Attached tools as (pluginId, toolId) pairs; must be empty after an unload.</summary>
    public ConcurrentDictionary<string, ToolContribution> Tools { get; } = new();

    /// <summary>Attached documents as (pluginId, documentId) pairs; must be empty after an unload.</summary>
    public ConcurrentDictionary<string, DocumentContribution> Documents { get; } = new();

    /// <summary>Attached menus keyed by plugin id and header.</summary>
    public ConcurrentDictionary<string, MenuContribution> Menus { get; } = new();

    public IDockableContribution AttachTool(string pluginId, ToolContribution tool)
    {
        var key = $"{pluginId}::{tool.Id}";
        Tools[key] = tool;
        return new DockableHandle(() => Tools.TryRemove(key, out _), () => Opened.Add(key));
    }

    public IDockableContribution AttachDocument(string pluginId, DocumentContribution document)
    {
        var key = $"{pluginId}::{document.Id}";
        Documents[key] = document;
        return new DockableHandle(() => Documents.TryRemove(key, out _), () => Opened.Add(key));
    }

    public IDisposable AttachMenu(string pluginId, MenuContribution menu)
    {
        var key = $"{pluginId}::{menu.Header}";
        Menus[key] = menu;
        return new Handle(() => Menus.TryRemove(key, out _));
    }

    /// <summary>Keys of contributions a plugin asked the host to open, so tests can assert on Open().</summary>
    public ConcurrentBag<string> Opened { get; } = [];

    private sealed class DockableHandle(Action undo, Action open) : IDockableContribution
    {
        private bool _disposed;

        public void Open()
        {
            if (!_disposed)
                open();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            undo();
        }
    }

    private sealed class Handle(Action undo) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            undo();
        }
    }
}

/// <summary>Minimal plugin services backed by an in-memory volume, mirroring the app's adapters.</summary>
internal sealed class TestServices : IPluginServices
{
    public TestServices(Big3DArray<byte>? volume)
    {
        Volumes = new TestVolumeService(volume);
        Masks = new TestMaskService(Volumes);
        Preview = new TestPreviewChannel();
    }

    public IVolumeService Volumes { get; }

    public IMaskService Masks { get; }

    public IPreviewChannel Preview { get; }

    public IPluginLog Log { get; } = new CollectingLog();

    /// <summary>The same instance as <see cref="Log"/>, typed so tests can read what was written.</summary>
    public CollectingLog Messages => (CollectingLog)Log;

    /// <summary>Optional capability a test can expose, mirroring the app's scripting host service.</summary>
    public object? AdditionalService { get; set; }

    public T? GetService<T>() where T : class => AdditionalService as T;
}

internal sealed class TestVolumeService : IVolumeService
{
    public TestVolumeService(Big3DArray<byte>? volume) => Current = volume;

    public ReadonlyBig3DArray? Current { get; private set; }

    public long Version { get; private set; }

    public event Action<long>? Replaced;

    public event Action? CrosshairChanged;

    public int CurrentX { get; private set; }

    public int CurrentY { get; private set; }

    public int CurrentZ { get; private set; }

    /// <summary>
    /// Same host contract as the app: the test host measures through the core implementation rather
    /// than growing a second histogram of its own.
    /// </summary>
    public long[]? ComputeHistogram(CancellationToken ct = default, Action<double>? progress = null)
        => Current is { } volume ? VolumeHistogram.Compute(volume, ct, progress) : null;

    public void Replace(ReadonlyBig3DArray? volume)
    {
        Current = volume;
        Version++;
        Replaced?.Invoke(Version);
    }

    public void MoveCrosshair(int x, int y, int z)
    {
        CurrentX = x;
        CurrentY = y;
        CurrentZ = z;
        CrosshairChanged?.Invoke();
    }
}

internal sealed class TestMaskService : IMaskService
{
    private readonly List<IMaskLayer> _layers = [];

    public TestMaskService(IVolumeService volumes) => Volumes = volumes;

    public IVolumeService Volumes { get; }

    public IReadOnlyList<IMaskLayer> Layers => _layers;

    public event Action? Changed;

    public event Action? ContentChanged;

    public int NotifyCount { get; private set; }

    /// <summary>
    /// Drops every layer and resets the notification count. Exists so a test that shares one service
    /// instance with others starts from a known state.
    /// </summary>
    public void Clear()
    {
        _layers.Clear();
        NotifyCount = 0;
    }

    public IMaskLayer Add(string name, Big3DBitMap bitmap, MaskColor color, double opacity = 0.4)
    {
        if (Volumes.Current is { } volume && !bitmap.MatchesDimensions(volume.X, volume.Y, volume.Z))
            throw new ArgumentException("Mask dimensions do not match the volume.", nameof(bitmap));

        var layer = new TestMaskLayer(name, bitmap, color, opacity);
        _layers.Add(layer);
        Changed?.Invoke();
        return layer;
    }

    public void Remove(IMaskLayer layer)
    {
        if (_layers.Remove(layer))
            Changed?.Invoke();
    }

    public void NotifyContentChanged()
    {
        NotifyCount++;
        ContentChanged?.Invoke();
    }
}

internal sealed class TestMaskLayer(string name, Big3DBitMap bitmap, MaskColor color, double opacity) : IMaskLayer
{
    public string Name { get; set; } = name;
    public MaskColor Color { get; set; } = color;
    public double Opacity { get; set; } = opacity;
    public bool IsVisible { get; set; } = true;
    public Big3DBitMap Bitmap { get; } = bitmap;
}

internal sealed class TestPreviewChannel : IPreviewChannel
{
    private readonly List<PreviewOverlay> _overlays = [];

    public IReadOnlyList<PreviewOverlay> Overlays => [.. _overlays];

    public event Action? Changed;

    public void Publish(PreviewOverlay overlay)
    {
        _overlays.RemoveAll(o => o.OwnerId == overlay.OwnerId && o.Plane == overlay.Plane && o.Index == overlay.Index);
        _overlays.Add(overlay);
        Changed?.Invoke();
    }

    public void Clear(string ownerId)
    {
        if (_overlays.RemoveAll(o => o.OwnerId == ownerId) > 0)
            Changed?.Invoke();
    }
}

/// <summary>
/// Log sink that keeps messages so tests can assert on warnings and errors. Structured templates
/// are flattened the way a one-line text sink would - the template, then any values - so a test can
/// keep asserting on plain fragments without the test project taking a logging dependency.
/// </summary>
internal sealed class CollectingLog : IPluginLog
{
    private readonly string _prefix;

    public CollectingLog()
        : this(new ConcurrentQueue<string>(), "")
    {
    }

    private CollectingLog(ConcurrentQueue<string> messages, string prefix)
    {
        Messages = messages;
        _prefix = prefix;
    }

    public ConcurrentQueue<string> Messages { get; }

    public void Debug(string template, params object?[] args)
        => Messages.Enqueue($"DBG {_prefix}{Flatten(template, args)}");

    public void Info(string template, params object?[] args)
        => Messages.Enqueue($"INF {_prefix}{Flatten(template, args)}");

    public void Warn(string template, params object?[] args)
        => Messages.Enqueue($"WRN {_prefix}{Flatten(template, args)}");

    public void Error(string template, Exception? exception = null, params object?[] args) =>
        Messages.Enqueue(
            $"ERR {_prefix}{Flatten(template, args)}{(exception == null ? "" : $" :: {exception.GetType().Name}")}");

    // Context stays textual here: a prefix is the simplest thing that keeps the source visible.
    public IPluginLog ForContext(string property, object? value)
        => new CollectingLog(Messages, $"{_prefix}[{property}={value}] ");

    public bool Contains(string fragment) => Messages.Any(m => m.Contains(fragment, StringComparison.Ordinal));

    private static string Flatten(string template, object?[] args) =>
        args.Length == 0 ? template : $"{template} [{string.Join(", ", args)}]";
}
