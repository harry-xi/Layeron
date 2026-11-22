using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Dock.Model.ReactiveUI.Controls;
using Avalonia.Controls;
using Layeron.App.Logging;
using Layeron.App.Models;
using Layeron.Big3DArray;
using ReactiveUI;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace Layeron.App.ViewModels;

public class MaskManagerViewModel : Tool
{
    private int _nextMaskIndex = 1;
    private SharedDataSourceViewModel? _sharedData;

    public MaskManagerViewModel()
    {
        Title = "Mask Manager";
        Id = "MaskManager";
        CanClose = true;
        CanDockAsDocument = false;

        AddMaskCommand = ReactiveCommand.Create(AddMask);
        RemoveMaskCommand = ReactiveCommand.Create<MaskLayer>(RemoveMask);
        LoadMaskCommand = ReactiveCommand.CreateFromTask(LoadMaskAsync);
        SaveMaskCommand = ReactiveCommand.CreateFromTask<MaskLayer>(SaveMaskAsync);

        // [TEMP-DEMO] never let a command failure break the app
        AddMaskCommand.ThrownExceptions.Subscribe(HandleCommandError);
        RemoveMaskCommand.ThrownExceptions.Subscribe(HandleCommandError);
        LoadMaskCommand.ThrownExceptions.Subscribe(HandleCommandError);
        SaveMaskCommand.ThrownExceptions.Subscribe(HandleCommandError);

        Masks.CollectionChanged += (_, _) =>
        {
            this.RaisePropertyChanged(nameof(LayersCountText));
            this.RaisePropertyChanged(nameof(HasNoMasks));
        };
    }

    // [TEMP-DEMO] log-and-swallow for mask commands
    private static void HandleCommandError(Exception ex)
    {
        Log.Error("Masks", ex, "Command failed");
    }

    public ObservableCollection<MaskLayer> Masks { get; } = [];

    public string LayersCountText => Masks.Count == 1 ? "1 mask" : $"{Masks.Count} masks";

    /// <summary>True when the panel should show its empty hint instead of a list.</summary>
    public bool HasNoMasks => Masks.Count == 0;

    /// <summary>The mask layer that slice tools and paint-style plugins write into.</summary>
    public MaskLayer? ActiveMask
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public SharedDataSourceViewModel? SharedData
    {
        get => _sharedData;
        set
        {
            if (_sharedData != null)
                _sharedData.PropertyChanged -= OnSharedDataChanged;
            _sharedData = value;
            if (value != null)
                value.PropertyChanged += OnSharedDataChanged;
        }
    }

    public ReactiveCommand<RxVoid, RxVoid> AddMaskCommand { get; }
    public ReactiveCommand<MaskLayer, RxVoid> RemoveMaskCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> LoadMaskCommand { get; }
    public ReactiveCommand<MaskLayer, RxVoid> SaveMaskCommand { get; }

    public event Action? MasksCollectionChanged;

    /// <summary>Raised when mask PIXELS change (not structure) - e.g. a tool gesture or a plugin wrote into a layer.</summary>
    public event Action? MasksContentChanged;

    /// <summary>Notifies every slice view that mask pixels changed; batch to once per gesture where possible.</summary>
    public void RaiseMasksContentChanged() => MasksContentChanged?.Invoke();

    private void OnSharedDataChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SharedDataSourceViewModel.Array))
            ValidateMaskDimensions();
    }

    private void ValidateMaskDimensions()
    {
        if (_sharedData?.Array == null) return;
        var ax = _sharedData.Array.X;
        var ay = _sharedData.Array.Y;
        var az = _sharedData.Array.Z;

        var removed = false;
        for (var i = Masks.Count - 1; i >= 0; i--)
        {
            if (!Masks[i].MatchesDimensions(ax, ay, az))
            {
                Masks.RemoveAt(i);
                removed = true;
            }
        }

        // Layers that do not fit the new volume disappear from the panel; listeners must see that too.
        if (removed)
            MasksCollectionChanged?.Invoke();

        if (ActiveMask != null && !Masks.Contains(ActiveMask))
            ActiveMask = Masks.FirstOrDefault(); // keep active mask valid
    }

    private void AddMask()
    {
        var ax = _sharedData?.Array;
        if (ax == null) return;

        var layer = new MaskLayer
        {
            Name = $"Mask {_nextMaskIndex++}",
            Bitmap = new Big3DBitMap(ax.X, ax.Y, ax.Z)
        };
        AddLayer(layer);
    }

    /// <summary>
    /// Adds a layer and notifies listeners. Host code (notably the plugin mask service) uses this so
    /// layers produced by plugins take exactly the same path as ones created in the panel.
    /// </summary>
    public void AddLayer(MaskLayer layer)
    {
        Masks.Add(layer);
        ActiveMask = layer;
        MasksCollectionChanged?.Invoke();
    }

    /// <summary>Removes a layer and notifies listeners.</summary>
    public void RemoveLayer(MaskLayer layer)
    {
        if (!Masks.Remove(layer))
            return;

        if (ReferenceEquals(ActiveMask, layer))
            ActiveMask = Masks.LastOrDefault(); // keep active mask valid
        MasksCollectionChanged?.Invoke();
    }

    private void RemoveMask(MaskLayer? layer)
    {
        if (layer == null) return;
        RemoveLayer(layer);
    }

    private async Task LoadMaskAsync()
    {
        var topLevel = GetTopLevel();
        if (topLevel == null) return;

        var volume = _sharedData?.Array;
        if (volume == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load Masks",
            // Several files can be selected in one dialog; they are appended in the order chosen.
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Mask Files") { Patterns = ["*.b3m"] },
                new FilePickerFileType("All Files") { Patterns = ["*.*"] }
            ]
        });

        if (files.Count == 0) return;

        var (x, y, z) = (volume.X, volume.Y, volume.Z);
        var loaded = 0;
        var failed = 0;

        foreach (var file in files)
        {
            var path = file.Path.LocalPath;

            try
            {
                var started = Stopwatch.StartNew();
                // Decoding a large mask takes long enough to notice; keep it off the UI thread the
                // same way the volume loader does.
                var layer = await Task.Run(() =>
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                        bufferSize: 1024 * 1024, FileOptions.SequentialScan);
                    return MaskLayer.LoadStream(fs, x, y, z);
                });
                var decodeMs = started.ElapsedMilliseconds;

                if (layer == null)
                {
                    // LoadStream returns null only for a dimensions mismatch. Saying so is the difference
                    // between "the file is fine, the data changed" and a load button that appears to do
                    // nothing.
                    failed++;
                    Log.Warn("Masks",
                        "Mask '{Path}' does not match the current volume {SizeX}x{SizeY}x{SizeZ}.",
                        path, x, y, z);
                    continue;
                }

                // Same path as every other layer, so the plugin mask service and the slice views learn
                // about a loaded mask exactly as they do about one drawn or produced by a plugin.
                AddLayer(layer);
                loaded++;
                Log.Info("Masks", "Loaded {Name} in {Decode} ms decode + {Refresh} ms UI refresh",
                    layer.Name, decodeMs, started.ElapsedMilliseconds - decodeMs);
            }
            catch (Exception ex)
            {
                // A malformed or truncated .b3m used to disappear without a trace. It stays non-fatal -
                // the session is untouched - and the remaining files are still attempted.
                failed++;
                Log.Error("Masks", ex, "Could not load mask from {Path}", path);
            }
        }

        if (files.Count > 1)
            Log.Info("Masks", "Loaded {Loaded} of {Total} masks ({Failed} failed)", loaded, files.Count, failed);
    }

    private async Task SaveMaskAsync(MaskLayer? layer)
    {
        if (layer?.Bitmap == null) return;

        var topLevel = GetTopLevel();
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Mask",
            SuggestedFileName = $"{layer.Name}.b3m",
            FileTypeChoices =
            [
                new FilePickerFileType("Mask Files") { Patterns = ["*.b3m"] }
            ]
        });

        if (file == null) return;

        try
        {
            var started = Stopwatch.StartNew();
            // Compression + disk I/O would otherwise freeze the UI while saving a large mask.
            await Task.Run(() =>
            {
                using var fs = new FileStream(file.Path.LocalPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    bufferSize: 1024 * 1024, FileOptions.SequentialScan);
                layer.SaveStream(fs);
            });
            Log.Info("Masks", "Saved {Name} in {Elapsed} ms", layer.Name, started.ElapsedMilliseconds);
        }
        catch
        {
            // swallow save errors silently
        }
    }

    private static TopLevel? GetTopLevel()
    {
        return TopLevel.GetTopLevel(
            Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow : null);
    }
}
