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

    /// <summary>Attached menus keyed by plugin id and header.</summary>
    public ConcurrentDictionary<string, MenuContribution> Menus { get; } = new();

    public IDisposable AttachTool(string pluginId, ToolContribution tool)
    {
        var key = $"{pluginId}::{tool.Id}";
        Tools[key] = tool;
        return new Handle(() => Tools.TryRemove(key, out _));
    }

    public IDisposable AttachMenu(string pluginId, MenuContribution menu)
    {
        var key = $"{pluginId}::{menu.Header}";
        Menus[key] = menu;
        return new Handle(() => Menus.TryRemove(key, out _));
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
    private readonly List<IMaskLayer> _layers = new();

    public TestMaskService(IVolumeService volumes) => Volumes = volumes;

    public IVolumeService Volumes { get; }

    public IReadOnlyList<IMaskLayer> Layers => _layers;

    public event Action? Changed;

    public event Action? ContentChanged;

    public int NotifyCount { get; private set; }

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
    private readonly List<PreviewOverlay> _overlays = new();

    public IReadOnlyList<PreviewOverlay> Overlays => _overlays.ToList();

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

/// <summary>Log sink that keeps messages so tests can assert on warnings and errors.</summary>
internal sealed class CollectingLog : IPluginLog
{
    public ConcurrentQueue<string> Messages { get; } = new();

    public void Debug(string message) => Messages.Enqueue($"DBG {message}");

    public void Info(string message) => Messages.Enqueue($"INF {message}");

    public void Warn(string message) => Messages.Enqueue($"WRN {message}");

    public void Error(string message, Exception? exception = null) =>
        Messages.Enqueue($"ERR {message}{(exception == null ? "" : $" :: {exception.GetType().Name}")}");

    public bool Contains(string fragment) => Messages.Any(m => m.Contains(fragment, StringComparison.Ordinal));
}
