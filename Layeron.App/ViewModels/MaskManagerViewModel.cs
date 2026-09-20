using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Dock.Model.ReactiveUI.Controls;
using Avalonia.Controls;
using Layeron.App.Models;
using Layeron.Big3DArray;
using ReactiveUI;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace Layeron.App.ViewModels;

public class MaskManagerViewModel : Tool
{
    private int _nextMaskIndex = 1;
    private SharedDataSourceViewModel? _sharedData;
    private MaskLayer? _activeMask; // [TEMP-DEMO]

    public MaskManagerViewModel()
    {
        Title = "Mask Manager";
        Id = "MaskManager";
        CanClose = true;

        AddMaskCommand = ReactiveCommand.Create(AddMask);
        RemoveMaskCommand = ReactiveCommand.Create<MaskLayer>(RemoveMask);
        LoadMaskCommand = ReactiveCommand.CreateFromTask(LoadMaskAsync);
        SaveMaskCommand = ReactiveCommand.CreateFromTask<MaskLayer>(SaveMaskAsync);

        // [TEMP-DEMO] never let a command failure break the app
        AddMaskCommand.ThrownExceptions.Subscribe(HandleCommandError);
        RemoveMaskCommand.ThrownExceptions.Subscribe(HandleCommandError);
        LoadMaskCommand.ThrownExceptions.Subscribe(HandleCommandError);
        SaveMaskCommand.ThrownExceptions.Subscribe(HandleCommandError);

        Masks.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(LayersCountText));
    }

    // [TEMP-DEMO] log-and-swallow for mask commands
    private static void HandleCommandError(Exception ex)
    {
        Console.WriteLine($"[MaskManager] command error: {ex}");
    }

    public ObservableCollection<MaskLayer> Masks { get; } = new();

    public string LayersCountText => $"{Masks.Count} layer(s)";

    // [TEMP-DEMO] mask painting support
    public int BrushRadius { get; set; } = 3;

    // [TEMP-DEMO] mask painting support
    public MaskLayer? ActiveMask
    {
        get => _activeMask;
        set => this.RaiseAndSetIfChanged(ref _activeMask, value);
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

    // [TEMP-DEMO] mask painting support: raised when mask PIXELS change (not structure)
    public event Action? MasksContentChanged;

    // [TEMP-DEMO] mask painting support
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

        for (int i = Masks.Count - 1; i >= 0; i--)
        {
            if (!Masks[i].MatchesDimensions(ax, ay, az))
                Masks.RemoveAt(i);
        }

        if (ActiveMask != null && !Masks.Contains(ActiveMask))
            ActiveMask = Masks.FirstOrDefault(); // [TEMP-DEMO] keep active mask valid
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
        ActiveMask = layer; // [TEMP-DEMO]
        MasksCollectionChanged?.Invoke();
    }

    /// <summary>Removes a layer and notifies listeners.</summary>
    public void RemoveLayer(MaskLayer layer)
    {
        if (!Masks.Remove(layer))
            return;

        if (ReferenceEquals(ActiveMask, layer))
            ActiveMask = Masks.LastOrDefault(); // [TEMP-DEMO] keep active mask valid
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

        var ax = _sharedData?.Array;
        if (ax == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load Mask",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Mask Files") { Patterns = new[] { "*.b3m" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });

        if (files.Count == 0) return;

        try
        {
            var path = files[0].Path.LocalPath;
            MaskLayer? layer;
            await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                             bufferSize: 1024 * 1024, FileOptions.SequentialScan))
            {
                layer = MaskLayer.LoadStream(fs, ax.X, ax.Y, ax.Z);
            }

            if (layer == null) return;
            Masks.Add(layer);
            ActiveMask = layer; // [TEMP-DEMO]
            MasksCollectionChanged?.Invoke();
        }
        catch
        {
            // swallow load errors silently
        }
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
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Mask Files") { Patterns = new[] { "*.b3m" } }
            }
        });

        if (file == null) return;

        try
        {
            await using var fs = new FileStream(file.Path.LocalPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 1024 * 1024, FileOptions.SequentialScan);
            layer.SaveStream(fs);
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
