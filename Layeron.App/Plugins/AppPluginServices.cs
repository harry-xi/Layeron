using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Layeron.App.Models;
using Layeron.App.Logging;
using Layeron.App.Session;
using Layeron.App.ViewModels;
using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;
using Layeron.Scripting.Abstractions;

namespace Layeron.App.Plugins;

/// <summary>
/// Application implementation of the plugin-facing services. Every member is an adapter over the
/// existing session objects rather than a new data model, so plugins and built-in tools observe and
/// mutate exactly the same state.
/// </summary>
public sealed class AppPluginServices : IPluginServices, IDisposable
{
    private readonly AppVolumeService _volumes;
    private readonly AppMaskService _masks;
    private readonly AppPreviewChannel _preview;

    public AppPluginServices(ISessionManager sessions, IPluginLog log)
    {
        Log = log;
        _volumes = new AppVolumeService(sessions);
        _masks = new AppMaskService(sessions, _volumes);
        _preview = new AppPreviewChannel();
    }

    public IVolumeService Volumes => _volumes;

    public IMaskService Masks => _masks;

    public IPreviewChannel Preview => _preview;

    public IPluginLog Log { get; }

    /// <summary>
    /// The scripting host capability handed to the script editor plugin. It is filled in after the
    /// plugin controller exists, because it delegates back to the controller for reloads.
    /// </summary>
    public IScriptHostService? ScriptHost { get; set; }

    public T? GetService<T>() where T : class => ScriptHost as T;

    public void Dispose()
    {
        // The script host service is a listener on the workspace manager; releasing it here keeps the
        // teardown in one place, next to the other services plugins were using.
        (ScriptHost as IDisposable)?.Dispose();

        _volumes.Dispose();
        _masks.Dispose();
    }
}

/// <summary>
/// Exposes the session's main volume as a versioned, read-only reference. The version counter is what
/// lets a plugin detect that a result it computed in the background belongs to a volume that has
/// already been replaced, instead of handing the user a stale mask.
/// </summary>
internal sealed class AppVolumeService : IVolumeService, IDisposable
{
    private readonly ISessionManager _sessions;
    private IDisposable? _sessionSubscription;
    private SharedDataSourceViewModel? _sharedData;

    public AppVolumeService(ISessionManager sessions)
    {
        _sessions = sessions;
        _sessionSubscription = sessions.CurrentSession.Subscribe(Attach);
    }

    public ReadonlyBig3DArray? Current => _sharedData?.Array;

    public long Version { get; private set; }

    public event Action<long>? Replaced;

    public event Action? CrosshairChanged;

    public int CurrentX => _sharedData?.CurrentX ?? 0;

    public int CurrentY => _sharedData?.CurrentY ?? 0;

    public int CurrentZ => _sharedData?.CurrentZ ?? 0;

    /// <inheritdoc />
    public long[]? ComputeHistogram(CancellationToken ct = default, Action<double>? progress = null)
        => Current is { } volume ? VolumeHistogram.Compute(volume, ct, progress) : null;

    private void Attach(ISession? session)
    {
        if (_sharedData != null)
        {
            _sharedData.PropertyChanged -= OnSharedDataChanged;
            _sharedData = null;
        }

        _sharedData = session?.SharedData;

        if (_sharedData != null)
            _sharedData.PropertyChanged += OnSharedDataChanged;

        // A new session is a new main volume as far as plugins are concerned.
        Version++;
        Replaced?.Invoke(Version);
    }

    private void OnSharedDataChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SharedDataSourceViewModel.Array):
                Version++;
                Replaced?.Invoke(Version);
                break;
            case nameof(SharedDataSourceViewModel.CurrentX):
            case nameof(SharedDataSourceViewModel.CurrentY):
            case nameof(SharedDataSourceViewModel.CurrentZ):
                CrosshairChanged?.Invoke();
                break;
        }
    }

    public void Replace(ReadonlyBig3DArray volume)
    {
        var shared = _sharedData ?? _sessions.Current?.SharedData
            ?? throw new InvalidOperationException("No active session to load into.");
        shared.Array = volume;
    }

    public void Dispose()
    {
        _sessionSubscription?.Dispose();
        _sessionSubscription = null;
        if (_sharedData != null)
            _sharedData.PropertyChanged -= OnSharedDataChanged;
        _sharedData = null;
    }
}

/// <summary>
/// Adapter over the session's mask manager. Added layers become ordinary <c>MaskLayer</c> instances,
/// so a mask a plugin produces behaves exactly like one the user created in the mask manager - it
/// persists after the plugin unloads, which is what you want for a segmentation result.
/// </summary>
internal sealed class AppMaskService : IMaskService, IDisposable
{
    private readonly ISessionManager _sessions;
    private readonly AppVolumeService _volumes;
    private readonly IDisposable? _sessionSubscription;
    private readonly List<IMaskLayer> _layers = [];

    public AppMaskService(ISessionManager sessions, AppVolumeService volumes)
    {
        _sessions = sessions;
        _volumes = volumes;
        _sessionSubscription = _sessions.CurrentSession.Subscribe(Attach);
    }

    public IReadOnlyList<IMaskLayer> Layers => _layers;

    public event Action? Changed;

    public event Action? ContentChanged;

    public IMaskLayer Add(string name, Big3DBitMap bitmap, MaskColor color, double opacity = 0.4)
    {
        var manager = _sessions.Current?.MaskManager
            ?? throw new InvalidOperationException("No active session.");

        if (_volumes.Current is { } volume && !bitmap.MatchesDimensions(volume.X, volume.Y, volume.Z))
        {
            throw new ArgumentException(
                $"Mask dimensions {bitmap.SizeX}x{bitmap.SizeY}x{bitmap.SizeZ} do not match the main volume " +
                $"{volume.X}x{volume.Y}x{volume.Z}.",
                nameof(bitmap));
        }

        var layer = new MaskLayer
        {
            Name = name,
            Color = Avalonia.Media.Color.FromRgb(color.R, color.G, color.B),
            Opacity = opacity,
            IsVisible = true,
            Bitmap = bitmap
        };

        manager.AddLayer(layer);
        _layers.Add(layer);
        Changed?.Invoke();
        return layer;
    }

    public void Remove(IMaskLayer layer)
    {
        if (layer is not MaskLayer typed)
            return;

        _sessions.Current?.MaskManager.RemoveLayer(typed);
        _layers.Remove(layer);
        Changed?.Invoke();
    }

    public void NotifyContentChanged()
    {
        _sessions.Current?.MaskManager.RaiseMasksContentChanged();
        ContentChanged?.Invoke();
    }

    private void Attach(ISession? session)
    {
        _layers.Clear();

        if (session?.MaskManager is { } manager)
        {
            foreach (var layer in manager.Masks)
                _layers.Add(layer);
        }

        // Layers are owned by the session: a new session starts with none of them.
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _sessionSubscription?.Dispose();
        _layers.Clear();
    }
}

/// <summary>
/// Holds transient overlays published by plugins. Cleared per owner, so unloading a plugin (which
/// clears its owner key) can never leave a ghost overlay in a slice view.
/// </summary>
internal sealed class AppPreviewChannel : IPreviewChannel
{
    private readonly List<PreviewOverlay> _overlays = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<PreviewOverlay> Overlays
    {
        get
        {
            lock (_gate)
            {
                return [.. _overlays];
            }
        }
    }

    public event Action? Changed;

    public void Publish(PreviewOverlay overlay)
    {
        lock (_gate)
        {
            // One overlay per (owner, plane, index): republishing replaces rather than accumulates,
            // so dragging a slider cannot grow the list.
            _overlays.RemoveAll(o =>
                string.Equals(o.OwnerId, overlay.OwnerId, StringComparison.Ordinal) &&
                o.Plane == overlay.Plane &&
                o.Index == overlay.Index);

            _overlays.Add(overlay);
        }

        Changed?.Invoke();
    }

    public void Clear(string ownerId)
    {
        bool removed;
        lock (_gate)
        {
            removed = _overlays.RemoveAll(o => string.Equals(o.OwnerId, ownerId, StringComparison.Ordinal)) > 0;
        }

        if (removed)
            Changed?.Invoke();
    }
}

/// <summary>
/// Routes plugin log messages into the app log under the <c>Plugin</c> category, so the log window
/// can isolate everything plugin-related with one filter. A thin adapter over the same Serilog
/// pipeline the host logs through: templates and structured values pass through untouched, and
/// <see cref="IPluginLog"/> stays free of any logging library's types - it is the plugin contract.
/// </summary>
internal sealed class ConsolePluginLog(Serilog.ILogger logger) : IPluginLog
{
    public ConsolePluginLog()
        : this(Log.ForCategory("Plugin"))
    {
    }

    public void Debug(string template, params object?[] args) => logger.Debug(template, args);

    public void Info(string template, params object?[] args) => logger.Information(template, args);

    public void Warn(string template, params object?[] args) => logger.Warning(template, args);

    public void Error(string template, Exception? exception = null, params object?[] args)
        => logger.Error(exception, template, args);

    public IPluginLog ForContext(string property, object? value)
        => new ConsolePluginLog(logger.ForContext(property, value));
}
