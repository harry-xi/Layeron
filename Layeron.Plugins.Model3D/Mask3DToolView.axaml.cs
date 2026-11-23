using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Aura3D.Model;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Layeron.Plugin.Abstractions;
using AuraModel = Aura3D.Core.Nodes.Model;

namespace Layeron.Plugins.Model3D;

/// <summary>
/// The model-management panel: mask selection, surface creation, the model list, import/export and
/// the actions that act on the selected model.
/// <para>
/// It is the only place that creates models. The 3D document reflects <see cref="Mask3DModelManager"/>
/// and owns no controls, which is what lets a user keep the tab open as a primary view while the
/// panel changes what it shows. Surface extraction and glTF parsing stay on background tasks; only
/// the finished node is created on the UI thread.
/// </para>
/// </summary>
public sealed partial class Mask3DToolView : UserControl, IDisposable
{
    /// <summary>Downsample factors offered in the panel; larger means a coarser, cheaper model.</summary>
    private static readonly int[] Factors = [1, 2, 4, 8, 16];

    /// <summary>
    /// Rebuild methods offered in the panel, in the same order as the combo box items.
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

    private static readonly FilePickerFileType GltfFileType = new("glTF 模型")
    {
        Patterns = ["*.glb", "*.gltf"],
        MimeTypes = ["model/gltf-binary", "model/gltf+json"],
    };

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

    private readonly IPluginServices? _services;
    private readonly Mask3DModelManager? _models;

    private readonly ComboBox? _layerBox;
    private readonly ComboBox? _factorBox;
    private readonly ComboBox? _methodBox;
    private readonly Button? _generateButton;
    private readonly Button? _cancelButton;
    private readonly Button? _importButton;
    private readonly Button? _showViewButton;
    private readonly ListBox? _modelList;
    private readonly TextBlock? _emptyModels;
    private readonly TextBlock? _modelCount;
    private readonly ProgressBar? _progress;
    private readonly TextBlock? _status;

    /// <summary>Colour views whose colour is being written by the panel rather than by the user.</summary>
    private readonly HashSet<ColorView> _syncingColors = [];

    private bool _disposed;
    private bool _building;
    private bool _importing;
    private CancellationTokenSource? _generation;

    /// <summary>Design-time / runtime-XAML constructor; builds the visual tree only.</summary>
    public Mask3DToolView()
    {
        InitializeComponent();
    }

    internal Mask3DToolView(IPluginServices services, Mask3DModelManager models)
    {
        _services = services;
        _models = models;

        InitializeComponent();

        _layerBox = this.FindControl<ComboBox>("LayerBox");
        _factorBox = this.FindControl<ComboBox>("FactorBox");
        _methodBox = this.FindControl<ComboBox>("MethodBox");
        _generateButton = this.FindControl<Button>("GenerateButton");
        _cancelButton = this.FindControl<Button>("CancelButton");
        _importButton = this.FindControl<Button>("ImportButton");
        _showViewButton = this.FindControl<Button>("ShowViewButton");
        _modelList = this.FindControl<ListBox>("ModelList");
        _emptyModels = this.FindControl<TextBlock>("EmptyModels");
        _modelCount = this.FindControl<TextBlock>("ModelCountText");
        _progress = this.FindControl<ProgressBar>("Progress");
        _status = this.FindControl<TextBlock>("StatusText");

        if (_factorBox is { } factorBox)
        {
            factorBox.ItemsSource = Array.ConvertAll(Factors, factor => $"{factor}×");
            factorBox.SelectedIndex = 2; // 4x: coarse enough to build quickly, fine enough to read
        }

        if (_methodBox is { } methodBox)
        {
            methodBox.ItemsSource = Array.ConvertAll(Methods, method => method.Label);
            methodBox.SelectedIndex = 0; // Marching Cubes: the smoother, shape-following surface
        }

        if (_modelList is { } list)
            list.ItemsSource = _models.Models;

        if (_generateButton is { } generate)
            generate.Click += OnGenerateClick;

        if (_cancelButton is { } cancel)
            cancel.Click += OnCancelClick;

        if (_importButton is { } import)
            import.Click += OnImportClick;

        if (_showViewButton is { } show)
            show.Click += OnShowViewClick;

        // Layers come and go with the session, so the list follows the host instead of being
        // snapshotted when the panel is created.
        services.Masks.Changed += OnMasksChanged;
        models.Changed += OnModelsChanged;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_services is null || _disposed)
            return;

        RefreshLayers();
        RefreshActions();
        SetStatus("已就绪");
    }

    private async void OnGenerateClick(object? sender, RoutedEventArgs e) => await GenerateAsync();

    /// <summary>
    /// Loads one or more glTF/GLB files and adds them to the model list. Parsing runs off the UI
    /// thread because reading external buffers and building the scene is file work; tangent
    /// completion and the manager entry happen back on the UI thread.
    /// </summary>
    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        if (_services is not { } services || _models is not { } models || _disposed || _importing)
            return;

        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;

        IReadOnlyList<IStorageFile> files;
        try
        {
            files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "导入 3D 模型",
                // Several files can be selected in one dialog; they are appended in the order chosen.
                AllowMultiple = true,
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

        var loaded = 0;
        var failed = 0;
        Mask3DModel? lastEntry = null;

        SetImporting(true);

        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                if (_disposed)
                    return;

                var path = files[i].TryGetLocalPath();
                if (string.IsNullOrEmpty(path))
                {
                    failed++;
                    services.Log.Warn("Mask 3D: '{File}' is not a local file.", files[i].Name);
                    continue;
                }

                SetStatus($"正在加载 {i + 1}/{files.Count}：{Path.GetFileName(path)}…");

                try
                {
                    var model = await Task.Run(() => LoadModel(path));
                    if (_disposed)
                        return;

                    var entry = models.AddImported(
                        model,
                        Path.GetFileNameWithoutExtension(path),
                        $"glTF · {model.Meshes.Count:N0} 个网格");

                    lastEntry = entry;
                    loaded++;
                }
                catch (Exception ex)
                {
                    // One unreadable file must not discard the files selected with it.
                    failed++;
                    services.Log.Error("Mask 3D: loading {Path} failed.", ex, path);
                }
            }

            if (lastEntry is not null && _modelList is { } list)
                list.SelectedItem = lastEntry;

            SetStatus((loaded, failed) switch
            {
                (0, > 0) => $"导入失败：{failed} 个文件未能读取",
                (_, > 0) => $"已导入 {loaded} 个模型，{failed} 个失败",
                _ when files.Count == 1 && lastEntry is not null => $"已添加 {lastEntry.Name}",
                _ => $"已导入 {loaded} 个模型"
            });
        }
        finally
        {
            if (!_disposed)
                SetImporting(false);
        }
    }

    private static AuraModel LoadModel(string path)
        => string.Equals(Path.GetExtension(path), ".glb", StringComparison.OrdinalIgnoreCase)
            ? ModelLoader.LoadGlbModel(path)
            : ModelLoader.LoadGltfModel(path);

    /// <summary>
    /// Saves one model from its row. Only extracted surfaces can be written: an imported model still
    /// has its original file, so re-encoding it would just lose data.
    /// </summary>
    private async void OnRowExportClick(object? sender, RoutedEventArgs e)
    {
        if (_disposed)
            return;

        if (sender is not Button { DataContext: Mask3DModel { Surface: { } surface } model })
            return;

        await ExportModelAsync(model, surface);
    }

    private async Task ExportModelAsync(Mask3DModel model, Mask3DGeometry.SurfaceMesh surface)
    {
        if (_services is not { } services || _disposed)
            return;

        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;

        IStorageFile? file;
        try
        {
            file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出 3D 模型",
                SuggestedFileName = SafeFileName(model.Name),
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
        var color = model.Color ?? Rgb.FromBytes(200, 100, 60);

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
    }

    private void OnRowRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (_disposed || _models is not { } models)
            return;

        if (sender is not Button { DataContext: Mask3DModel model })
            return;

        if (models.Remove(model))
            SetStatus($"已删除 {model.Name}");
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (_disposed || _generation is not { } generation)
            return;

        generation.Cancel();
        SetStatus("正在取消…");
    }

    private void OnShowViewClick(object? sender, RoutedEventArgs e) => _models?.RevealView?.Invoke();

    private async Task GenerateAsync()
    {
        if (_services is not { } services || _models is not { } models || _disposed)
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
        // should see the last one they asked for, not a queue of stale models. The superseded request
        // owns no shared state, so it can be cancelled without waiting for it to stop.
        var generation = new CancellationTokenSource();
        var previous = _generation;
        _generation = generation;
        previous?.Cancel();

        var token = generation.Token;

        SetBuilding(true);
        SetStatus($"正在生成 {factor}× {methodLabel} 模型…");

        try
        {
            // Pure array work, so it goes to a background task and the window stays responsive. The
            // vertex streams are built there too: binding them to engine objects is the only part that
            // has to happen back on the UI thread.
            var result = await Task.Run(
                () =>
                {
                    var downsampled = Mask3DGeometry.DownsampleCropped(bitmap, factor, token);
                    var stats = downsampled.Stats;

                    // Debug-only diagnostics: whether the crop actually paid off for this mask, and how
                    // much of the storage was empty or fully set. The panel is not the place to show it.
                    services.Log.Debug(
                        "Mask 3D: {Method} {Factor}x solid={HasSolid} grid={GridX}x{GridY}x{GridZ} of {VolumeX}x{VolumeY}x{VolumeZ} cells; blocks {Occupied}/{Total} (full {Full}).",
                        methodLabel,
                        factor,
                        downsampled.HasSolid,
                        downsampled.Grid.GetLength(0),
                        downsampled.Grid.GetLength(1),
                        downsampled.Grid.GetLength(2),
                        downsampled.Frame.VolumeX,
                        downsampled.Frame.VolumeY,
                        downsampled.Frame.VolumeZ,
                        stats.OccupiedBlocks,
                        stats.TotalBlocks,
                        stats.FullBlocks);

                    var mesh = Mask3DGeometry.BuildSurface(downsampled, method, factor, token);
                    var buffers = Mask3DScene.BuildGeometryBuffers(mesh, token);
                    return (Mesh: mesh, Buffers: buffers);
                },
                token);

            if (_disposed || token.IsCancellationRequested)
                return;

            var mesh = result.Mesh;

            if (mesh.TriangleCount == 0)
            {
                SetStatus("该掩膜没有可显示的表面");
                return;
            }

            var name = string.IsNullOrWhiteSpace(layer.Name) ? "掩膜模型" : layer.Name;
            var model = models.AddSurface(
                mesh,
                color,
                name,
                $"{mesh.SolidVoxels:N0} 体素 · {mesh.TriangleCount:N0} 三角面 · {factor}× {methodLabel}",
                result.Buffers);

            if (_modelList is { } list)
                list.SelectedItem = model;

            SetStatus($"已添加 {model.Name}");
        }
        catch (OperationCanceledException)
        {
            // Superseded or explicitly cancelled. Only the request that still owns the slot reports it;
            // a superseded one leaves the status line to the newer request.
            if (ReferenceEquals(_generation, generation))
                SetStatus("已取消");
        }
        catch (Exception ex)
        {
            services.Log.Error("Mask 3D: building the model failed.", ex);

            if (ReferenceEquals(_generation, generation))
                SetStatus($"生成失败：{ex.Message}");
        }
        finally
        {
            // Only the newest request owns the building state. A cancelled one may finish after a
            // newer request has already taken over, and must not clear that request's progress.
            if (ReferenceEquals(_generation, generation))
            {
                _generation = null;
                SetBuilding(false);
            }

            generation.Dispose();
        }
    }

    /// <summary>Rebuilds the layer list, keeping the current selection when it still exists.</summary>
    private void RefreshLayers()
    {
        if (_services is not { } services || _layerBox is not { } box)
            return;

        var names = services.Masks.Layers
            .Select(layer => string.IsNullOrWhiteSpace(layer.Name) ? "(未命名掩膜)" : layer.Name)
            .ToList();

        if (names.Count == 0)
            names.Add("(无掩膜层)");

        var previous = box.SelectedIndex;
        box.ItemsSource = names;
        box.SelectedIndex = previous >= 0 && previous < names.Count ? previous : 0;

        RefreshActions();
    }

    private void OnMasksChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
                RefreshLayers();
        });
    }

    private void OnModelsChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
                RefreshActions();
        });
    }

    /// <summary>
    /// Pushes a model's colour into the picker that lives in its row. Called when the row is created,
    /// before any user edit, so the call itself must not count as an edit.
    /// </summary>
    private void OnRowColorLoaded(object? sender, RoutedEventArgs e)
    {
        if (_disposed || sender is not ColorView view || view.DataContext is not Mask3DModel model)
            return;

        SyncRowColor(view, model);
    }

    private void OnRowColorChanged(object? sender, ColorChangedEventArgs e)
    {
        if (_disposed || sender is not ColorView view || _syncingColors.Contains(view))
            return;

        if (view.DataContext is not Mask3DModel model || _models is not { } models)
            return;

        var color = e.NewColor;
        if (models.SetColor(model, Rgb.FromBytes(color.R, color.G, color.B)))
            SetStatus($"已更新 {model.Name} 的颜色");
    }

    private void SyncRowColor(ColorView view, Mask3DModel model)
    {
        _syncingColors.Add(view);

        try
        {
            view.Color = model.Color is { } color
                ? Color.FromRgb(
                    RgbBrushConverter.ToByte(color.R),
                    RgbBrushConverter.ToByte(color.G),
                    RgbBrushConverter.ToByte(color.B))
                : Colors.Gray;
        }
        finally
        {
            _syncingColors.Remove(view);
        }
    }

    private void SetBuilding(bool building)
    {
        _building = building;
        RefreshActions();
    }

    private void SetImporting(bool importing)
    {
        _importing = importing;
        RefreshActions();
    }

    /// <summary>
    /// Keeps the toolbar buttons and the progress bar consistent with both operations. Model
    /// management is deliberately not gated: removing, recolouring, hiding or exporting a model does
    /// not touch the data a build or an import is working on.
    /// </summary>
    private void RefreshActions()
    {
        var count = _models?.Models.Count ?? 0;
        var hasLayers = _services?.Masks.Layers.Count > 0;

        // A second build supersedes the first, so the button stays available while one is running.
        if (_generateButton is { } generate)
            generate.IsEnabled = hasLayers;

        if (_cancelButton is { } cancel)
        {
            cancel.IsVisible = _building;
            cancel.IsEnabled = _building;
        }

        if (_importButton is { } import)
            import.IsEnabled = !_importing;

        if (_progress is { } progress)
            progress.IsVisible = _building || _importing;

        if (_emptyModels is { } hint)
            hint.IsVisible = count == 0;

        if (_modelCount is { } modelCount)
            modelCount.Text = $"{count} 个";
    }

    private void SetStatus(string text)
    {
        if (_status is { } status)
            status.Text = text;
    }

    /// <summary>A mask colour is 0-255 sRGB; the renderer wants normalised 0-1 floats.</summary>
    private static Rgb ToColor(MaskColor color) => Rgb.FromBytes(color.R, color.G, color.B);

    /// <summary>The model name becomes a suggested file name, so it must survive the file system.</summary>
    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);

        foreach (var ch in name)
            builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);

        return builder.Length == 0 ? "mask3d" : builder.ToString();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Called by the host when the panel closes or the plugin unloads. A build still running is
    /// cancelled and its result discarded, so a late mesh cannot land in an unwired view. Models
    /// already created stay in the manager - closing the panel is not the same as clearing the scene.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_services is { } services)
            services.Masks.Changed -= OnMasksChanged;

        if (_models is { } models)
            models.Changed -= OnModelsChanged;

        if (_generateButton is { } generate)
            generate.Click -= OnGenerateClick;

        if (_cancelButton is { } cancel)
            cancel.Click -= OnCancelClick;

        if (_importButton is { } import)
            import.Click -= OnImportClick;

        if (_showViewButton is { } show)
            show.Click -= OnShowViewClick;

        _syncingColors.Clear();

        // Cancel but do not dispose: the build task owns the source and disposes it in its own
        // finally. Disposing here would race a cancellation callback still registered by the worker.
        _generation?.Cancel();
        _generation = null;
    }
}
