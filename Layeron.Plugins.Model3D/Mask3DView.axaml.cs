using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using HelixToolkit.Nex.Avalonia;
using HelixToolkit.Nex.Maths;
using Layeron.Plugin.Abstractions;

namespace Layeron.Plugins.Model3D;

/// <summary>
/// The document tab: a toolbar over the Helix viewport, plus the lifetime of the 3D engine behind it.
/// <para>
/// All engine work runs on one dedicated worker thread (see <see cref="EngineThread"/>). The engine's
/// world is single-threaded and remembers the thread that created it, and scene construction blocks, so
/// neither the UI thread nor an arbitrary thread-pool thread may touch it.
/// </para>
/// </summary>
public sealed partial class Mask3DView : UserControl, IDisposable
{
    /// <summary>Downsample factors offered in the toolbar; larger means a coarser, cheaper model.</summary>
    private static readonly int[] Factors = [1, 2, 4, 8, 16];

    private readonly IPluginServices? _services;
    private readonly EngineThread? _engineThread;

    private readonly HelixViewport? _viewport;
    private readonly ComboBox? _layerBox;
    private readonly ComboBox? _factorBox;
    private readonly Button? _generateButton;
    private readonly ProgressBar? _progress;
    private readonly TextBlock? _status;

    private Mask3DScene? _scene;
    private bool _disposed;

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
        _engineThread = new EngineThread();

        InitializeComponent();

        // The view is built from XAML loaded by name lookup rather than generated fields, matching the
        // other plugin views in this repository.
        _viewport = this.FindControl<HelixViewport>("Viewport");
        _layerBox = this.FindControl<ComboBox>("LayerBox");
        _factorBox = this.FindControl<ComboBox>("FactorBox");
        _generateButton = this.FindControl<Button>("GenerateButton");
        _progress = this.FindControl<ProgressBar>("Progress");
        _status = this.FindControl<TextBlock>("StatusText");

        if (_factorBox is { } factorBox)
        {
            factorBox.ItemsSource = Array.ConvertAll(Factors, f => $"{f}×");
            factorBox.SelectedIndex = 2; // 4x: coarse enough to build quickly, fine enough to read
        }

        if (_generateButton is { } button)
            button.Click += OnGenerateClick;

        // Layers come and go with the session, so the list follows the host's notifications instead of
        // being snapshotted once when the tab is created.
        _services.Masks.Changed += OnMasksChanged;

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // The parameterless constructor only builds the visual tree, so a designer instance has no host
        // and no engine thread: it must stop here rather than start an engine.
        if (_services is not { } services || _engineThread is not { } engineThread)
            return;

        RefreshLayers();

        if (_disposed || _scene is not null)
            return;

        SetBusy(true);
        SetStatus("正在初始化 3D 引擎…");

        try
        {
            var scene = await engineThread.InvokeAsync(static () => new Mask3DScene());

            if (_disposed)
            {
                await engineThread.InvokeAsync(() =>
                {
                    scene.Dispose();
                    return true;
                });
                return;
            }

            _scene = scene;

            if (_viewport is { } viewport)
            {
                viewport.Engine = scene.Engine;
                viewport.CameraController = scene.CameraController;
                viewport.ViewportClient = scene.Client;
            }

            SetStatus("已就绪");

            // Show something immediately: a tab that needs a manual click before it proves it works is a
            // poor first impression, and the default factor is cheap enough to build on open.
            if (services.Masks.Layers.Count > 0)
                await GenerateAsync();
        }
        catch (Exception ex)
        {
            services.Log.Error("Mask 3D: the 3D engine could not be started.", ex);
            SetStatus($"3D 引擎启动失败：{ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnGenerateClick(object? sender, RoutedEventArgs e) => await GenerateAsync();

    private async Task GenerateAsync()
    {
        if (_services is not { } services || _engineThread is not { } engineThread || _disposed || _scene is null)
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
        var color = ToColor4(layer.Color);
        var factor = Factors[Math.Clamp(_factorBox?.SelectedIndex ?? 0, 0, Factors.Length - 1)];

        SetBusy(true);
        SetStatus($"正在生成 {factor}× 模型…");

        try
        {
            // Downsampling and surface extraction are pure array work, but they run on the engine thread
            // too: the mesh goes straight into the world, which may only ever be touched by that thread.
            var mesh = await engineThread.InvokeAsync(() =>
            {
                if (_scene is not { } scene)
                    return null;

                var grid = Mask3DGeometry.Downsample(bitmap, factor, CancellationToken.None);
                var surface = Mask3DGeometry.BuildSurface(grid, CancellationToken.None);
                scene.SetMesh(surface, color);
                return surface;
            });

            if (mesh is not null)
                SetStatus($"{mesh.SolidVoxels:N0} 体素 · {mesh.TriangleCount:N0} 三角面（{factor}×）");
        }
        catch (Exception ex)
        {
            services.Log.Error("Mask 3D: building the model failed.", ex);
            SetStatus($"生成失败：{ex.Message}");
        }
        finally
        {
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
        if (_progress is { } progress)
            progress.IsVisible = busy;

        if (_generateButton is { } button)
            button.IsEnabled = !busy && _services?.Masks.Layers.Count > 0;
    }

    private void SetStatus(string text)
    {
        if (_status is { } status)
            status.Text = text;
    }

    /// <summary>A mask colour is 0-255 sRGB; the material wants linear-ish 0-1 floats.</summary>
    private static Color4 ToColor4(MaskColor color)
        => new(color.R / 255f, color.G / 255f, color.B / 255f, 1f);

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Called by the host when the tab closes or the plugin unloads. The engine is released here, not in
    /// a visual-tree event: a tab can be detached and re-attached while the dock rearranges, and that must
    /// not tear the engine down.
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

        // The viewport owns the per-viewport render context and the GPU interop resources, and it must let
        // go of the engine before the engine is disposed (it deliberately never disposes the engine itself).
        _viewport?.Dispose();

        try
        {
            // Engine-side teardown belongs to the thread that owns the world.
            _engineThread?.InvokeAsync(() =>
            {
                _scene?.Dispose();
                _scene = null;
                return true;
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _services?.Log.Error("Mask 3D: releasing the 3D engine failed.", ex);
        }

        _engineThread?.Dispose();
    }

    /// <summary>
    /// A single background thread that owns every engine and world mutation.
    /// <para>
    /// The engine's world is single-threaded: it is bound to the thread that created it, which is why the
    /// scene cannot simply be built with <c>Task.Run</c> (the pool would hand out a different thread for
    /// the next rebuild). Work is queued here and runs strictly in order on one thread.
    /// </para>
    /// </summary>
    private sealed class EngineThread : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;

        public EngineThread()
        {
            _thread = new Thread(Pump)
            {
                IsBackground = true,
                Name = "Mask3D.Engine"
            };
            _thread.Start();
        }

        /// <summary>Runs <paramref name="work"/> on the engine thread and completes with its result.</summary>
        public Task<T> InvokeAsync<T>(Func<T> work)
        {
            // RunContinuationsAsynchronously: the awaiting thread is the UI thread, and completing the task
            // inline here would run UI continuations on the engine thread.
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(() =>
            {
                try
                {
                    completion.SetResult(work());
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            });
            return completion.Task;
        }

        private void Pump()
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                // A failure is already reported through the task; the pump must survive it so a single bad
                // rebuild does not take the engine thread down with it.
                work();
            }
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(5));
            _queue.Dispose();
        }
    }
}
