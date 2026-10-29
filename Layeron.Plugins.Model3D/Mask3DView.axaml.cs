using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Aura3D.Model;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Layeron.Plugin.Abstractions;
using AuraModel = Aura3D.Core.Nodes.Model;

namespace Layeron.Plugins.Model3D;

/// <summary>
/// The document tab: a toolbar over the 3D viewport.
/// <para>
/// The model is produced by <see cref="Mask3DGeometry"/> (pure CPU array work) and drawn by Aura3D
/// through the OpenGL context Avalonia already hosts, so there is no bespoke rasteriser and no second
/// windowing stack. Downsampling and surface extraction run on a background task so a large mask never
/// blocks the UI; only the finished mesh crosses back to the UI thread.
/// </para>
/// <para>
/// The toolbar can also load a glTF/GLB file into the same viewport, which is what the
/// <c>Aura3D.Model.GltfLoader</c> dependency is for.
/// </para>
/// </summary>
public sealed partial class Mask3DView : UserControl, IDisposable
{
    /// <summary>Downsample factors offered in the toolbar; larger means a coarser, cheaper model.</summary>
    private static readonly int[] Factors = [1, 2, 4, 8, 16];

    /// <summary>
    /// Rebuild methods offered in the toolbar, in the same order as the combo box items.
    /// <para>
    /// Marching Cubes leads the list because it is the default view: it follows the shape of the mask
    /// instead of showing the sampling grid it came from, and the exact voxel-face extractor stays
    /// available for when that blockiness is the point.
    /// </para>
    /// </summary>
    private static readonly (string Label, Mask3DGeometry.ExtractionMethod Method)[] Methods =
    [
        ("游走立方体", Mask3DGeometry.ExtractionMethod.MarchingCubes),
        ("体素面", Mask3DGeometry.ExtractionMethod.VoxelFaces),
    ];

    private readonly IPluginServices? _services;

    private readonly Mask3DViewport? _viewport;
    private readonly ComboBox? _layerBox;
    private readonly ComboBox? _factorBox;
    private readonly ComboBox? _methodBox;
    private readonly Button? _generateButton;
    private readonly Button? _importButton;
    private readonly Button? _exportButton;
    private readonly ProgressBar? _progress;
    private readonly TextBlock? _status;

    /// <summary>File types offered by "导入模型". Both are handled by the same loader.</summary>
    private static readonly FilePickerFileType GltfFileType = new("glTF 模型")
    {
        Patterns = ["*.glb", "*.gltf"],
        MimeTypes = ["model/gltf-binary", "model/gltf+json"],
    };

    /// <summary>Save formats: STL for downstream mesh tools, GLB when the colour and normals matter.</summary>
    private static readonly FilePickerFileType StlFileType = new("STL 网格")
    {
        Patterns = ["*.stl"],
        MimeTypes = ["model/stl"],
    };

    private static readonly FilePickerFileType GlbFileType = new("glTF 二进制")
    {
        Patterns = ["*.glb"],
        MimeTypes = ["model/gltf-binary"],
    };

    /// <summary>The last generated surface, kept so it can be exported without rebuilding it.</summary>
    private Mask3DGeometry.SurfaceMesh? _surface;
    private Rgb _surfaceColor;

    private bool _disposed;
    private bool _busy;
    private CancellationTokenSource? _generation;

    /// <summary>
    /// Design-time / runtime-XAML constructor.
    /// <para>
    /// A XAML resource with an <c>x:Class</c> must expose a public parameterless constructor, otherwise
    /// the runtime loader cannot reach it at all (AVLN3001). It builds the visual tree and nothing else:
    /// the host always creates this view through the services overload below, so no engine thread is
    /// started and no mask handler is wired here.
    /// </para>
    /// </summary>
    public Mask3DView()
    {
        InitializeComponent();
    }

    public Mask3DView(IPluginServices services)
    {
        _services = services;

        InitializeComponent();

        // The view is built from XAML loaded by name lookup rather than generated fields, matching the
        // other plugin views in this repository.
        _viewport = this.FindControl<Mask3DViewport>("Viewport");
        _layerBox = this.FindControl<ComboBox>("LayerBox");
        _factorBox = this.FindControl<ComboBox>("FactorBox");
        _methodBox = this.FindControl<ComboBox>("MethodBox");
        _generateButton = this.FindControl<Button>("GenerateButton");
        _importButton = this.FindControl<Button>("ImportButton");
        _exportButton = this.FindControl<Button>("ExportButton");
        _progress = this.FindControl<ProgressBar>("Progress");
        _status = this.FindControl<TextBlock>("StatusText");

        if (_factorBox is { } factorBox)
        {
            factorBox.ItemsSource = Array.ConvertAll(Factors, f => $"{f}×");
            factorBox.SelectedIndex = 2; // 4x: coarse enough to build quickly, fine enough to read
        }

        if (_methodBox is { } methodBox)
        {
            methodBox.ItemsSource = Array.ConvertAll(Methods, m => m.Label);
            methodBox.SelectedIndex = 0; // Marching Cubes: the smoother, shape-following surface
        }

        if (_generateButton is { } button)
            button.Click += OnGenerateClick;

        if (_importButton is { } import)
            import.Click += OnImportClick;

        if (_exportButton is { } export)
            export.Click += OnExportClick;

        // Layers come and go with the session, so the list follows the host's notifications instead of
        // being snapshotted once when the tab is created.
        _services.Masks.Changed += OnMasksChanged;

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // The parameterless constructor only builds the visual tree, so a designer instance has no host
        // and no engine thread: it must stop here rather than start an engine.
        if (_services is not { } services)
            return;

        if (_disposed)
            return;

        RefreshLayers();

        SetStatus("已就绪");

        try
        {
            if (services.Masks.Layers.Count > 0)
                await GenerateAsync();
        }
        catch (Exception ex)
        {
            services.Log.Error("Mask 3D: building the model failed.", ex);
            SetStatus($"生成失败：{ex.Message}");
        }
    }

    private async void OnGenerateClick(object? sender, RoutedEventArgs e) => await GenerateAsync();

    /// <summary>
    /// Loads a glTF/GLB file into the viewport, replacing whatever the tab currently shows.
    /// <para>
    /// This is the second source of geometry: the mask surface and an imported model are both plain
    /// Aura3D scenes, so the same viewport, camera and interaction serve either. Parsing runs off the
    /// UI thread because reading external buffers and building the scene is file work.
    /// </para>
    /// </summary>
    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        if (_services is not { } services || _disposed)
            return;

        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;

        IReadOnlyList<IStorageFile> files;
        try
        {
            files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "导入 3D 模型",
                AllowMultiple = false,
                FileTypeFilter = [GltfFileType],
            });
        }
        catch (Exception ex)
        {
            services.Log.Error("Mask 3D: opening the model picker failed.", ex);
            SetStatus($"选择文件失败：{ex.Message}");
            return;
        }

        if (files.Count == 0 || _disposed)
            return;

        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            SetStatus("只能加载本地文件");
            return;
        }

        SetBusy(true);
        SetStatus($"正在加载 {Path.GetFileName(path)}…");

        try
        {
            var model = await Task.Run(() => LoadModel(path));
            if (_disposed)
                return;

            _viewport?.SetModel(model);

            // An imported model replaces the generated surface, so it must stop being exportable.
            _surface = null;
            UpdateExportState();

            SetStatus($"已加载 {Path.GetFileName(path)} · {model.Meshes.Count:N0} 个网格");
        }
        catch (Exception ex)
        {
            services.Log.Error("Mask 3D: loading the glTF model failed.", ex);
            SetStatus($"加载失败：{ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static AuraModel LoadModel(string path)
        => string.Equals(Path.GetExtension(path), ".glb", StringComparison.OrdinalIgnoreCase)
            ? ModelLoader.LoadGlbModel(path)
            : ModelLoader.LoadGltfModel(path);

    /// <summary>
    /// Saves the surface that is currently on screen.
    /// <para>
    /// The extension decides the format: <c>.stl</c> is geometry only and is what downstream mask
    /// tools expect, <c>.glb</c> keeps the per-face normals and the mask colour. The surface is the
    /// one from the last successful generation, so exporting never re-runs the extraction.
    /// </para>
    /// </summary>
    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (_services is not { } services || _disposed || _surface is not { } surface)
            return;

        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;

        IStorageFile? file;
        try
        {
            file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出 3D 模型",
                SuggestedFileName = "mask3d",
                DefaultExtension = "stl",
                ShowOverwritePrompt = true,
                FileTypeChoices = [StlFileType, GlbFileType],
            });
        }
        catch (Exception ex)
        {
            services.Log.Error("Mask 3D: opening the export picker failed.", ex);
            SetStatus($"选择文件失败：{ex.Message}");
            return;
        }

        if (file is null || _disposed)
            return;

        var path = file.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            SetStatus("只能导出到本地文件");
            return;
        }

        if (string.IsNullOrEmpty(Path.GetExtension(path)))
            path += ".stl";

        var glb = string.Equals(Path.GetExtension(path), ".glb", StringComparison.OrdinalIgnoreCase);
        var color = _surfaceColor;

        SetBusy(true);
        SetStatus($"正在导出 {Path.GetFileName(path)}…");

        try
        {
            await Task.Run(() =>
            {
                if (glb)
                {
                    Mask3DExport.WriteGlb(path, surface, color);
                }
                else
                {
                    using var stream = File.Create(path);
                    Mask3DExport.WriteStl(stream, surface);
                }
            });

            if (_disposed)
                return;

            var size = new FileInfo(path).Length;
            SetStatus($"已导出 {Path.GetFileName(path)} · {surface.TriangleCount:N0} 三角面 · {size / 1024.0:N0} KB");
        }
        catch (Exception ex)
        {
            services.Log.Error("Mask 3D: exporting the model failed.", ex);
            SetStatus($"导出失败：{ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task GenerateAsync()
    {
        if (_services is not { } services || _disposed)
            return;

        var layers = services.Masks.Layers;
        var index = _layerBox?.SelectedIndex ?? -1;
        if (index < 0 || index >= layers.Count)
        {
            SetStatus("没有可用的掩膜层");
            return;
        }

        var layer = layers[index];
        var bitmap = layer.Bitmap;
        var color = ToColor(layer.Color);
        var factor = Factors[Math.Clamp(_factorBox?.SelectedIndex ?? 0, 0, Factors.Length - 1)];
        var methodIndex = Math.Clamp(_methodBox?.SelectedIndex ?? 0, 0, Methods.Length - 1);
        var method = Methods[methodIndex].Method;
        var methodLabel = Methods[methodIndex].Label;

        // A new request supersedes any build still running: a user who keeps changing the factor
        // should see the last one they asked for, not a queue of stale models.
        var previous = _generation;
        _generation = new CancellationTokenSource();
        previous?.Cancel();
        previous?.Dispose();

        var token = _generation.Token;

        SetBusy(true);
        SetStatus($"正在生成 {factor}× {methodLabel} 模型…");

        try
        {
            // Pure array work, so it goes to a background task and the window stays responsive.
            var mesh = await Task.Run(
                () =>
                {
                    var grid = Mask3DGeometry.Downsample(bitmap, factor, token);
                    return Mask3DGeometry.BuildSurface(grid, method, token);
                },
                token);

            if (_disposed || token.IsCancellationRequested)
                return;

            _viewport?.SetMesh(mesh, color);

            // Keep exactly what is on screen so "导出模型" cannot export a stale or different surface.
            _surface = mesh.TriangleCount > 0 ? mesh : null;
            _surfaceColor = color;
            UpdateExportState();

            SetStatus($"{mesh.SolidVoxels:N0} 体素 · {mesh.TriangleCount:N0} 三角面（{factor}× {methodLabel}）");
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer request; the newer one will set its own status.
        }
        catch (Exception ex)
        {
            services.Log.Error("Mask 3D: building the model failed.", ex);
            SetStatus($"生成失败：{ex.Message}");
        }
        finally
        {
            // Only the newest request owns the busy flag, so a cancelled one must not clear it.
            if (!token.IsCancellationRequested)
                SetBusy(false);
        }
    }

    /// <summary>Rebuilds the layer list, keeping the current selection when it still exists.</summary>
    private void RefreshLayers()
    {
        if (_services is not { } services || _layerBox is not { } box)
            return;

        var names = new List<string>();
        foreach (var layer in services.Masks.Layers)
            names.Add(string.IsNullOrWhiteSpace(layer.Name) ? "(未命名掩膜)" : layer.Name);

        if (names.Count == 0)
            names.Add("(无掩膜层)");

        var previous = box.SelectedIndex;
        box.ItemsSource = names;
        box.SelectedIndex = previous >= 0 && previous < names.Count ? previous : 0;

        if (_generateButton is { } button)
            button.IsEnabled = services.Masks.Layers.Count > 0;
    }

    private void OnMasksChanged()
    {
        // Raised on whatever thread changed the masks; the list is UI state, so marshal it.
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
                RefreshLayers();
        });
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;

        if (_progress is { } progress)
            progress.IsVisible = busy;

        if (_generateButton is { } button)
            button.IsEnabled = !busy && _services?.Masks.Layers.Count > 0;

        if (_importButton is { } import)
            import.IsEnabled = !busy;

        UpdateExportState();
    }

    /// <summary>Export is available only while a generated surface is the thing on screen.</summary>
    private void UpdateExportState()
    {
        if (_exportButton is { } export)
            export.IsEnabled = !_busy && !_disposed && _surface is not null && _surface.TriangleCount > 0;
    }

    private void SetStatus(string text)
    {
        if (_status is { } status)
            status.Text = text;
    }

    /// <summary>A mask colour is 0-255 sRGB; the renderer wants normalised 0-1 floats.</summary>
    private static Rgb ToColor(MaskColor color) => Rgb.FromBytes(color.R, color.G, color.B);

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Called by the host when the tab closes or the plugin unloads. Anything still building is
    /// cancelled and its result discarded, so a late-arriving mesh cannot touch a torn-down view.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_services is { } services)
            services.Masks.Changed -= OnMasksChanged;

        if (_generateButton is { } button)
            button.Click -= OnGenerateClick;

        if (_importButton is { } import)
            import.Click -= OnImportClick;

        if (_exportButton is { } export)
            export.Click -= OnExportClick;

        _generation?.Cancel();
        _generation?.Dispose();
        _generation = null;
    }
}

