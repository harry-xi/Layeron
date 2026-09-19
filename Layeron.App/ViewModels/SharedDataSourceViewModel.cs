using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Layeron.Big3DArray;
using ReactiveUI;
using RxVoid = ReactiveUI.Primitives.RxVoid;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

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
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync);
        GenerateDemoCommand = ReactiveCommand.Create(GenerateDemoData);
        GenerateDemo16Command = ReactiveCommand.Create(GenerateDemoData16);
        CreateFromImagesCommand = ReactiveCommand.CreateFromTask(CreateFromImagesAsync);

        LoadCommand.ThrownExceptions.Subscribe(OnDataCommandError);
        SaveCommand.ThrownExceptions.Subscribe(OnDataCommandError);
        GenerateDemoCommand.ThrownExceptions.Subscribe(OnDataCommandError);
        GenerateDemo16Command.ThrownExceptions.Subscribe(OnDataCommandError);
        CreateFromImagesCommand.ThrownExceptions.Subscribe(OnDataCommandError);
    }

    // [TEMP-DEMO] never let a load/generate command failure break the app
    private void OnDataCommandError(Exception ex)
    {
        StatusText = $"Error: {ex.Message}";
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
        }
    }

    private static string DescribeElementType(ReadonlyBig3DArray array)
    {
        return array.ElementType == PixelElementType.UInt16 ? "(16-bit)" : "(8-bit)";
    }

    public MaskManagerViewModel? MaskManager { get; set; }

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
            FileTypeFilter = new[]
            {
                new FilePickerFileType("3D Array Files") { Patterns = new[] { "*.b3d", "*.gz" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });

        if (files.Count > 0)
        {
            var path = files[0].Path.LocalPath;
            StatusText = "Loading...";
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
            }
        }
    }

    private async Task SaveAsync()
    {
        if (_array == null) return;

        var topLevel = GetTopLevel();
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save 3D Array File",
            SuggestedFileName = Path.GetFileName(FilePath ?? "volume.b3d"),
            FileTypeChoices = new[]
            {
                new FilePickerFileType("3D Array Files") { Patterns = new[] { "*.b3d" } }
            }
        });

        if (file != null)
        {
            var path = file.Path.LocalPath;
            StatusText = "Saving...";
            try
            {
                await Task.Run(() =>
                {
                    using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                        bufferSize: 1024 * 1024, FileOptions.SequentialScan);
                    _array.SaveStream(fs);
                });
                FilePath = path;
                StatusText = $"Saved to {Path.GetFileName(path)}";
            }
            catch (Exception ex)
            {
                StatusText = $"Error: {ex.Message}";
            }
        }
    }

    private void GenerateDemoData()
    {
        var array = new Big3DArray<byte>(128, 128, 64);
        array.Fill((x, y, z) =>
        {
            int v = (int)(
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
        var array = new Big3DArray<ushort>(128, 128, 64);
        array.Fill((x, y, z) =>
        {
            int v = (int)(
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
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Image Files") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.tiff", "*.tif", "*.webp" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });

        if (files.Count == 0) return;

        StatusText = $"Loading {files.Count} images...";
        
        try
        {
            var imagePaths = files.Select(f => f.Path.LocalPath).ToList();
            
            var array = await Task.Run(() =>
            {
                byte[,] ImageToBytes(string path)
                {
                    using var image = Image.Load<L8>(path);
                    var w = image.Width;
                    var h = image.Height;
                    var data = new byte[h, w];
                    image.ProcessPixelRows(accessor =>
                    {
                        for (int y = 0; y < h; y++)
                        {
                            var row = accessor.GetRowSpan(y);
                            for (int x = 0; x < w; x++)
                                data[y, x] = row[x].PackedValue;
                        }
                    });
                    return data;
                }

                using var first = Image.Load<L8>(imagePaths[0]);
                return Big3DArray<byte>.FromImages(imagePaths, ImageToBytes, first.Width, first.Height, imagePaths.Count);
            });

            if (array != null)
            {
                Array = array;
                FilePath = null;
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
    }

    private static Avalonia.Controls.TopLevel? GetTopLevel()
    {
        return Avalonia.Controls.TopLevel.GetTopLevel(
            Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow : null);
    }
}