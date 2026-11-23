using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Layeron.Plugin.Abstractions;

namespace Layeron.Plugins.ModelSmooth;

/// <summary>
/// The smoothing panel: picks an algorithm and its parameters, reads a model from the 3D plugin's
/// public list, runs the algorithm and writes the result back as a new model.
/// <para>
/// Parameters live here, not in the 3D plugin, because they belong to the algorithm: Taubin has
/// iterations plus a lambda/mu pair, the plain Laplacian has iterations and lambda (and shrinks, so
/// it is the strong-cleanup choice), and a future simplifier would have a target triangle count or
/// an error budget. A generic transform point would have forced every algorithm into one shape of
/// parameters; owning the panel keeps each plugin free to expose exactly its own knobs.
/// </para>
/// <para>
/// The panel never touches scene nodes. It reads immutable snapshots from
/// <see cref="IModel3DModelList"/>, does the heavy work on a background task, and hands the new
/// meshes back through <see cref="IModel3DModelList.Add"/> - the 3D plugin builds the node and the
/// list raises <see cref="IModel3DModelList.Changed"/>, which refreshes this list in turn.
/// </para>
/// </summary>
public sealed partial class ModelSmoothView : UserControl, IDisposable
{
    private readonly IModel3DModelList? _models;
    private readonly IPluginServices? _services;

    private readonly ComboBox? _algorithmBox;
    private readonly ListBox? _modelList;
    private readonly TextBlock? _emptyHint;
    private readonly TextBlock? _modelCount;
    private readonly NumericUpDown? _iterations;
    private readonly NumericUpDown? _lambda;
    private readonly NumericUpDown? _mu;
    private readonly Grid? _muRow;
    private readonly ProgressBar? _progress;
    private readonly TextBlock? _status;

    /// <summary>The two algorithms the panel can run; order matches the combo box items.</summary>
    private static readonly (string Label, SmoothingKind Kind)[] Algorithms =
    [
        ("Taubin（保形）", SmoothingKind.Taubin),
        ("Laplacian（强平滑，可能收缩）", SmoothingKind.Laplacian),
    ];

    private enum SmoothingKind
    {
        Taubin,
        Laplacian,
    }

    private bool _disposed;
    private bool _busy;
    private CancellationTokenSource? _smoothing;

    /// <summary>Design-time / runtime-XAML constructor; builds the visual tree only.</summary>
    public ModelSmoothView()
    {
        InitializeComponent();
    }

    internal ModelSmoothView(IModel3DModelList models, IPluginServices services)
    {
        _models = models;
        _services = services;

        InitializeComponent();

        _algorithmBox = this.FindControl<ComboBox>("AlgorithmBox");
        _modelList = this.FindControl<ListBox>("ModelList");
        _emptyHint = this.FindControl<TextBlock>("EmptyHint");
        _modelCount = this.FindControl<TextBlock>("ModelCountText");
        _iterations = this.FindControl<NumericUpDown>("IterationBox");
        _lambda = this.FindControl<NumericUpDown>("LambdaBox");
        _mu = this.FindControl<NumericUpDown>("MuBox");
        _muRow = this.FindControl<Grid>("MuRow");
        _progress = this.FindControl<ProgressBar>("Progress");
        _status = this.FindControl<TextBlock>("StatusText");

        if (_algorithmBox is { } algorithmBox)
        {
            algorithmBox.ItemsSource = Array.ConvertAll(Algorithms, algorithm => algorithm.Label);

            // Laplacian is the default because it is the one that actually removes the broad
            // marching-cubes terracing this panel was written for.
            algorithmBox.SelectedIndex = 1;
        }

        models.Changed += OnModelsChanged;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_disposed)
            return;

        RefreshModels();
        SetStatus("读取现有模型，平滑结果会作为新模型加入 3D 模型列表");
    }

    private void OnModelsChanged()
    {
        // The manager raises Changed on the UI thread, but the panel can be created from a restored
        // layout; posting keeps the ItemsSource swap on the dispatcher either way.
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
                RefreshModels();
        });
    }

    private void RefreshModels()
    {
        if (_disposed || _models is null)
            return;

        var models = _models.Models;

        if (_modelList is { } list)
            list.ItemsSource = models.ToList();

        if (_emptyHint is { } hint)
            hint.IsVisible = models.Count == 0;

        if (_modelCount is { } count)
            count.Text = $"{models.Count} 个";
    }

    private async void OnSmoothRowClick(object? sender, RoutedEventArgs e)
    {
        if (_disposed || _busy || _models is not { } models)
            return;

        if (sender is not Button { DataContext: Model3DContribution model })
            return;

        var parameters = ReadParameters();
        var label = parameters.Kind == SmoothingKind.Taubin ? "Taubin" : "Laplacian";

        var previous = _smoothing;
        _smoothing = new CancellationTokenSource();
        previous?.Cancel();
        previous?.Dispose();

        var token = _smoothing.Token;

        SetBusy(true);
        SetStatus($"正在对 {model.Name} 应用 {label}…");

        try
        {
            var meshes = await Task.Run(
                () =>
                {
                    var result = new List<Model3DMesh>(model.Meshes.Count);

                    foreach (var mesh in model.Meshes)
                    {
                        token.ThrowIfCancellationRequested();
                        result.Add(Apply(mesh, parameters, token));
                    }

                    return result;
                },
                token);

            if (_disposed || token.IsCancellationRequested)
                return;

            var triangles = meshes.Sum(mesh => mesh.TriangleCount);
            var detail = parameters.Kind == SmoothingKind.Taubin
                ? $"Taubin · {parameters.Iterations} 次 · λ={parameters.Lambda:0.00} · μ={parameters.Mu:0.00} · {triangles:N0} 三角面"
                : $"Laplacian · {parameters.Iterations} 次 · λ={parameters.Lambda:0.00} · {triangles:N0} 三角面";

            var added = models.Add($"{model.Name}（平滑）", detail, meshes, model.Color);

            if (added is null)
            {
                SetStatus("平滑结果没有可显示的三角面");
                return;
            }

            SetStatus($"已添加 {added.Name} · {label} · {triangles:N0} 三角面");
        }
        catch (OperationCanceledException)
        {
            // Superseded by a new request or by the panel closing; that owner sets its own status.
        }
        catch (Exception ex)
        {
            _services?.Log.Error("Model Smooth: smoothing the model failed.", ex);
            SetStatus($"平滑失败：{ex.Message}");
        }
        finally
        {
            if (!token.IsCancellationRequested)
                SetBusy(false);
        }
    }

    private static Model3DMesh Apply(
        Model3DMesh mesh,
        SmoothingParameters parameters,
        CancellationToken ct)
        => parameters.Kind == SmoothingKind.Taubin
            ? ModelSmoothing.Smooth(mesh, parameters.Iterations, parameters.Lambda, parameters.Mu, ct)
            : ModelSmoothing.Laplacian(mesh, parameters.Iterations, parameters.Lambda, ct);

    /// <summary>
    /// Reads the parameters of the selected algorithm. Lambda is shared, mu exists only for Taubin
    /// and is pushed below -lambda when the user drags it too close; Laplacian has no extra knob.
    /// The iteration cap is the UI's, not the kernel's.
    /// </summary>
    private SmoothingParameters ReadParameters()
    {
        var kind = CurrentKind();
        var iterations = (int)Math.Clamp(_iterations?.Value ?? 30m, 1m, 200m);
        var lambda = (float)Math.Clamp(_lambda?.Value ?? 0.5m, 0.01m, 0.99m);

        var mu = 0f;

        if (kind == SmoothingKind.Taubin)
        {
            mu = (float)Math.Clamp(_mu?.Value ?? -0.53m, -0.99m, -0.01m);

            if (mu >= -lambda)
                mu = -lambda - 0.03f;
        }

        return new SmoothingParameters(kind, iterations, lambda, mu);
    }

    private SmoothingKind CurrentKind()
    {
        if (_algorithmBox is { SelectedIndex: >= 0 } box && box.SelectedIndex < Algorithms.Length)
            return Algorithms[box.SelectedIndex].Kind;

        return SmoothingKind.Laplacian;
    }

    /// <summary>Shows the mu row only for Taubin; Laplacian has no extra parameter.</summary>
    private void OnAlgorithmChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_disposed)
            return;

        var laplacian = CurrentKind() == SmoothingKind.Laplacian;

        if (_muRow is { } muRow)
            muRow.IsVisible = !laplacian;
    }

    private readonly record struct SmoothingParameters(
        SmoothingKind Kind,
        int Iterations,
        float Lambda,
        float Mu);

    private void SetBusy(bool busy)
    {
        _busy = busy;

        if (_progress is { } progress)
            progress.IsVisible = busy;

        if (_modelList is { } list)
            list.IsEnabled = !busy;
    }

    private void SetStatus(string text)
    {
        if (_status is { } status)
            status.Text = text;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Called by the host when the panel closes or the plugin unloads: drop the list subscription
    /// (the list belongs to another plugin and outlives this view) and cancel a running pass.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_models is { } models)
            models.Changed -= OnModelsChanged;

        _smoothing?.Cancel();
        _smoothing?.Dispose();
        _smoothing = null;
    }
}
