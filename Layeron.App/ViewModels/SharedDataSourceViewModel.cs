using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Layeron.Big3DArray;
using Layeron.App.Logging;
using Layeron.App.Plugins;
using Layeron.App.Service;
using Layeron.Plugin.Abstractions;
using ReactiveUI;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace Layeron.App.ViewModels;

public class SharedDataSourceViewModel : ViewModelBase
{
    private ReadonlyBig3DArray? _array;
    private int _currentX;
    private int _currentY;
    private int _currentZ;

    public SharedDataSourceViewModel()
    {
        LoadCommand = ReactiveCommand.CreateFromTask(LoadAsync);
        SaveCommand = ReactiveCommand.CreateFromTask(() => SaveAsync(VolumeCompression.None));
        SaveCompressedCommand = ReactiveCommand.CreateFromTask(() => SaveAsync(VolumeCompression.GZip));
        GenerateDemoCommand = ReactiveCommand.Create(GenerateDemoData);
        GenerateDemo16Command = ReactiveCommand.Create(GenerateDemoData16);
        CreateFromImagesCommand = ReactiveCommand.CreateFromTask(CreateFromImagesAsync);

        LoadCommand.ThrownExceptions.Subscribe(OnDataCommandError);
        SaveCommand.ThrownExceptions.Subscribe(OnDataCommandError);
        SaveCompressedCommand.ThrownExceptions.Subscribe(OnDataCommandError);
        GenerateDemoCommand.ThrownExceptions.Subscribe(OnDataCommandError);
        GenerateDemo16Command.ThrownExceptions.Subscribe(OnDataCommandError);
        CreateFromImagesCommand.ThrownExceptions.Subscribe(OnDataCommandError);
    }

    // [TEMP-DEMO] never let a load/generate command failure break the app
    private void OnDataCommandError(Exception ex)
    {
        StatusText = $"Error: {ex.Message}";
        Log.Error("Data", ex, "Data command failed");
    }

    public ReadonlyBig3DArray? Array
    {
        get => _array;
        set
        {
            this.RaiseAndSetIfChanged(ref _array, value);
            this.RaisePropertyChanged(nameof(HasData));
            this.RaisePropertyChanged(nameof(MaxX));
            this.RaisePropertyChanged(nameof(MaxY));
            this.RaisePropertyChanged(nameof(MaxZ));
            CurrentX = 0;
            CurrentY = 0;
            CurrentZ = 0;
            StatusText = value != null
                ? $"Loaded: {value.X}x{value.Y}x{value.Z} {DescribeElementType(value)}"
                : "No data loaded";

            // The volume's shape and element type are exactly the facts a bug report needs, and they
            // are logged as named properties so the log window can filter for them.
            if (value != null)
            {
                Log.Info("Data", "Volume loaded: {SizeX}x{SizeY}x{SizeZ} {ElementType}",
                    value.X, value.Y, value.Z, value.ElementType);
            }
            else
            {
                Log.Info("Data", "Volume cleared; no data loaded");
            }
        }
    }

    private static string DescribeElementType(ReadonlyBig3DArray array)
    {
        return array.ElementType == PixelElementType.UInt16 ? "(16-bit)" : "(8-bit)";
    }

    public MaskManagerViewModel? MaskManager { get; set; }

    /// <summary>
    /// Session annotation manager. It is stored here as well as on the session so a view that already
    /// holds the data source (the slice views do) can reach the interaction state with no extra lookup.
    /// </summary>
    public AnnotationManagerViewModel? AnnotationManager { get; set; }

    public bool HasData => _array != null;

    public int MaxX => _array?.X - 1 ?? 0;
    public int MaxY => _array?.Y - 1 ?? 0;
    public int MaxZ => _array?.Z - 1 ?? 0;

    public int CurrentX
    {
        get => _currentX;
        set
        {
            var max = MaxX;
            var v = Math.Clamp(value, 0, max);
            this.RaiseAndSetIfChanged(ref _currentX, v);
            this.RaisePropertyChanged(nameof(CrosshairSagittal));
        }
    }

    public int CurrentY
    {
        get => _currentY;
        set
        {
            var max = MaxY;
            var v = Math.Clamp(value, 0, max);
            this.RaiseAndSetIfChanged(ref _currentY, v);
            this.RaisePropertyChanged(nameof(CrosshairCoronal));
        }
    }

    public int CurrentZ
    {
        get => _currentZ;
        set
        {
            var max = MaxZ;
            var v = Math.Clamp(value, 0, max);
            this.RaiseAndSetIfChanged(ref _currentZ, v);
            this.RaisePropertyChanged(nameof(CrosshairAxial));
        }
    }

    public int CrosshairAxial => _currentZ;
    public int CrosshairCoronal => _currentY;
    public int CrosshairSagittal => _currentX;

    public string StatusText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "No data loaded";

    public string? FilePath
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public ReactiveCommand<RxVoid, RxVoid> LoadCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> SaveCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> SaveCompressedCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> GenerateDemoCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> GenerateDemo16Command { get; }
    public ReactiveCommand<RxVoid, RxVoid> CreateFromImagesCommand { get; }

    private async Task LoadAsync()
    {
        var topLevel = GetTopLevel();
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open 3D Array File",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("3D Array Files") { Patterns = ["*.b3d", "*.gz"] },
                new FilePickerFileType("All Files") { Patterns = ["*.*"] }
            ]
        });

        if (files.Count > 0)
        {
            var path = files[0].Path.LocalPath;
            StatusText = "Loading...";
            Log.Info("Data", "Loading volume from {Path}", path);
            try
            {
                var array = await Task.Run(() =>
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                        bufferSize: 1024 * 1024, FileOptions.SequentialScan);
                    return Big3DArrayFactory.LoadStream(fs);
                });
                Array = array;
                FilePath = path;
            }
            catch (Exception ex)
            {
                StatusText = $"Error: {ex.Message}";
                Log.Error("Data", ex, "Failed to load {Path}", path);
            }
        }
    }

    private async Task SaveAsync(VolumeCompression compression)
    {
        var array = _array;
        if (array == null) return;

        var topLevel = GetTopLevel();
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = compression == VolumeCompression.GZip ? "Save 3D Array File (GZip)" : "Save 3D Array File",
            SuggestedFileName = Path.GetFileName(FilePath ?? "volume.b3d"),
            FileTypeChoices =
            [
                new FilePickerFileType("3D Array Files") { Patterns = ["*.b3d"] }
            ]
        });

        if (file != null)
        {
            var path = file.Path.LocalPath;
            StatusText = compression == VolumeCompression.GZip ? "Saving (GZip)..." : "Saving...";
            try
            {
                await Task.Run(() =>
                {
                    using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                        bufferSize: 1024 * 1024, FileOptions.SequentialScan);
                    array.SaveStream(fs, compression);
                });
                FilePath = path;
                StatusText = compression == VolumeCompression.GZip
                    ? $"Saved (GZip) to {Path.GetFileName(path)}"
                    : $"Saved to {Path.GetFileName(path)}";
                Log.Info("Data", "Volume saved to {Path} ({Compression})", path, compression);
            }
            catch (Exception ex)
            {
                StatusText = $"Error: {ex.Message}";
                Log.Error("Data", ex, "Failed to save to {Path}", path);
            }
        }
    }

    private void GenerateDemoData()
    {
        Log.Debug("Data", "Generating 8-bit demo volume 128x128x64");
        var array = new Big3DArray<byte>(128, 128, 64);
        array.Fill((x, y, z) =>
        {
            var v = (int)(
                Math.Sin(x * 0.1) * 40 +
                Math.Cos(y * 0.1) * 40 +
                Math.Sin(z * 0.15) * 40 +
                128);
            return (byte)Math.Clamp(v, 0, 255);
        });
        Array = array;
        FilePath = null;
    }

    private void GenerateDemoData16()
    {
        Log.Debug("Data", "Generating 16-bit demo volume 128x128x64");
        var array = new Big3DArray<ushort>(128, 128, 64);
        array.Fill((x, y, z) =>
        {
            var v = (int)(
                Math.Sin(x * 0.1) * 8000 +
                Math.Cos(y * 0.1) * 8000 +
                Math.Sin(z * 0.15) * 8000 +
                32768);
            return (ushort)Math.Clamp(v, 0, 65535);
        });
        Array = array;
        FilePath = null;
    }

    private async Task CreateFromImagesAsync()
    {
        var topLevel = GetTopLevel();
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Image Files (in slice order)",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Image Files") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.tiff", "*.tif", "*.webp"
                    ]
                },
                new FilePickerFileType("All Files") { Patterns = ["*.*"] }
            ]
        });

        if (files.Count == 0) return;

        StatusText = $"Loading {files.Count} images...";
        Log.Info("Data", "Building a volume from {Count} image(s)", files.Count);

        try
        {
            var imagePaths = files.Select(f => f.Path.LocalPath).ToList();

            var (array, info) = await Task.Run(() =>
            {
                var set = ImageVolumeLoader.Probe(imagePaths);
                return (ImageVolumeLoader.Load(imagePaths, set), set);
            });

            Array = array;
            FilePath = null;
            StatusText = info.Is16Bit
                ? $"Loaded {info.BitsPerComponent}-bit volume from {imagePaths.Count} images"
                : $"Loaded volume from {imagePaths.Count} images";
            Log.Info("Data", "Built a {Bits}-bit volume {X}x{Y}x{Z} from {Count} image(s)",
                info.BitsPerComponent, info.Width, info.Height, imagePaths.Count);
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            Log.Error("Data", ex, "Failed to build a volume from images");
        }
    }

    private static Avalonia.Controls.TopLevel? GetTopLevel()
    {
        return Avalonia.Controls.TopLevel.GetTopLevel(
            Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow : null);
    }

    /// <summary>
    /// The volume loaders the import menu offers: the built-in readers plus every one a plugin
    /// contributed. Wired once, after the plugin host exists, because the session - and therefore this
    /// view model - is created before any plugin is loaded.
    /// </summary>
    public IReadOnlyList<VolumeLoader> Loaders
    {
        get => _loaders;
        set
        {
            _loaders = value;
            this.RaisePropertyChanged();
        }
    }

    private IReadOnlyList<VolumeLoader> _loaders = [];

    /// <summary>
    /// Loads through one of the registered loaders. The picker's shape comes from the loader itself
    /// (files or a folder, which patterns), and the reading itself runs through the shared pipeline,
    /// so a contributed format is reached exactly like a built-in one.
    /// </summary>
    public async Task ImportAsync(VolumeLoader loader)
    {
        var topLevel = GetTopLevel();
        if (topLevel == null)
            return;

        IReadOnlyList<string> paths;
        string? directory = null;

        if (loader.SelectsFolder)
        {
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = loader.DisplayName,
                AllowMultiple = false
            });

            if (folders.Count == 0)
                return;

            directory = folders[0].Path.LocalPath;
            paths = [directory];
        }
        else
        {
            var filter = loader.FilePatterns.Count > 0
                ? [new FilePickerFileType(loader.DisplayName) { Patterns = [.. loader.FilePatterns] }]
                : DefaultFileTypes;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = loader.DisplayName,
                AllowMultiple = loader.AllowsMultipleFiles,
                FileTypeFilter = filter
            });

            if (files.Count == 0)
                return;

            paths = [.. files.Select(f => f.Path.LocalPath)];
        }

        StatusText = $"正在通过 {loader.DisplayName} 读取…";

        try
        {
            // Building the volume runs off the UI thread; adopting it is a plain assignment, which
            // is what refreshes the slice views, the window/level range and the status bar.
            var volume = await Task.Run(() => VolumeLoaderRunner.Load(loader, paths, directory));
            if (volume is null)
            {
                StatusText = "该数据源没有读取到内容";
                return;
            }

            Array = volume;
            FilePath = directory ?? paths.FirstOrDefault();
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            Log.Error("Data", ex, "Loader {Loader} failed", loader.Id);
        }
    }

    private static IReadOnlyList<FilePickerFileType> DefaultFileTypes =>
        [new FilePickerFileType("All Files") { Patterns = ["*.*"] }];
}