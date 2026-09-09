using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Platform.Storage;
using Dock.Model.ReactiveUI.Controls;
using Avalonia.Controls;
using Layeron.App.Models;
using Layeron.Big3DArray;
using ReactiveUI;

namespace Layeron.App.ViewModels;

public class MaskManagerViewModel : Tool
{
    private int _nextMaskIndex = 1;
    private SharedDataSourceViewModel? _sharedData;

    public MaskManagerViewModel()
    {
        Title = "Mask Manager";
        Id = "MaskManager";
        CanClose = false;

        AddMaskCommand = ReactiveCommand.Create(AddMask);
        RemoveMaskCommand = ReactiveCommand.Create<MaskLayer>(RemoveMask);
        LoadMaskCommand = ReactiveCommand.CreateFromTask(LoadMaskAsync);
        SaveMaskCommand = ReactiveCommand.CreateFromTask<MaskLayer>(SaveMaskAsync);
    }

    public ObservableCollection<MaskLayer> Masks { get; } = new();

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

    public ICommand AddMaskCommand { get; }
    public ICommand RemoveMaskCommand { get; }
    public ICommand LoadMaskCommand { get; }
    public ICommand SaveMaskCommand { get; }

    public event Action? MasksCollectionChanged;
    public event Action<MaskLayer>? MaskPropertyChanged;

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
        Masks.Add(layer);
        MasksCollectionChanged?.Invoke();
    }

    private void RemoveMask(MaskLayer? layer)
    {
        if (layer == null) return;
        Masks.Remove(layer);
        MasksCollectionChanged?.Invoke();
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

    internal void NotifyMaskPropertyChanged(MaskLayer layer)
    {
        MaskPropertyChanged?.Invoke(layer);
    }

    private static TopLevel? GetTopLevel()
    {
        return TopLevel.GetTopLevel(
            Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow : null);
    }
}
