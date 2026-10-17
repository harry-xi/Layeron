using System.Diagnostics;
using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;
using Layeron.Plugins.Onnx.Api;

namespace Layeron.Plugins.Onnx.Runtime;

/// <summary>
/// The published ONNX runner: executes a model over a plane, across a whole volume, or on a tensor the caller
/// supplied.
/// <para>
/// Every public entry point hands the work to <see cref="OnnxInferenceQueue"/>, so nothing runs on the caller's
/// thread and a UI caller can await a run without freezing the window. That is the third time this project has
/// needed the shape, so it is the one the Python runner settled on after measuring the alternatives.
/// </para>
/// <para>
/// The class holds no threading state of its own, so each run path is directly callable from a test with no queue
/// involved at all.
/// </para>
/// </summary>
internal sealed class OnnxRunnerService : IOnnxRunnerService, IDisposable
{
    private readonly OnnxModelStore _store;
    private readonly OnnxSettings _settings;
    private readonly IVolumeService _volumes;
    private readonly OnnxInferenceQueue _queue = new();
    private bool _disposed;

    public OnnxRunnerService(OnnxModelStore store, OnnxSettings settings, IVolumeService volumes)
    {
        _store = store;
        _settings = settings;
        _volumes = volumes;
    }

    /// <summary>Folder the bundled demo models live in, set during activation before any run can happen.</summary>
    public string PluginDataDirectory { get; set; } = string.Empty;

    /// <summary>Workspace root at activation time, so a workspace-level model folder is found.</summary>
    public string? WorkspaceRoot { get; set; }

    /// <inheritdoc />
    public IReadOnlyList<OnnxModelInfo> Models => _store.Models;

    /// <inheritdoc />
    public event Action? ModelsChanged
    {
        add => _store.Changed += value;
        remove => _store.Changed -= value;
    }

    /// <summary>Models that could not be loaded, with the reason, for the settings page and the log to report.</summary>
    public IReadOnlyList<(string Path, string Reason)> Failures => _store.Failures;

    /// <inheritdoc />
    public OnnxModelInfo? GetModel(string idOrNameOrPath) => _store.Find(idOrNameOrPath)?.Info;

    /// <inheritdoc />
    public Task<int> RescanAsync(CancellationToken cancellationToken = default) =>
        _queue.Enqueue(() =>
        {
            // The bundled models are reconciled on every scan rather than loaded once at startup: a scan is the
            // panel's "show me what I can run", and a list that dropped them after the first user scan would be
            // answering a different question.
            _store.UseEmbedded(OnnxDemoModels.Read(_ => { }));
            return _store.Rescan(_settings.ModelFolders(PluginDataDirectory, WorkspaceRoot));
        }, cancellationToken);


    /// <inheritdoc />
    public Task<OnnxSliceResult> RunSliceAsync(OnnxSliceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _queue.Enqueue(() => RunSlice(request), cancellationToken);
    }

    /// <inheritdoc />
    public Task<OnnxVolumeResult> RunVolumeAsync(OnnxVolumeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _queue.Enqueue(() => RunVolume(request, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<OnnxRunResult> RunAsync(OnnxRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _queue.Enqueue(() => RunRaw(request), cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _queue.Dispose();
    }

    /// <summary>
    /// Runs one plane and returns it in the plane's own coordinates.
    /// <para>
    /// The model's input size decides the work: a graph that accepts any size is one call, and a graph with a fixed
    /// size is tiled and stitched back together. The caller gets the same picture either way - which is the point,
    /// because the panel, the preview and the mask path all go through here and none of them should have to know
    /// which kind of model the user picked.
    /// </para>
    /// </summary>
    public OnnxSliceResult RunSlice(OnnxSliceRequest request)
    {
        var entry = Require(request.ModelId);
        var volume = RequireVolume();
        var binding = _settings.BindingFor(entry.Info);
        var mapping = ResolveMapping(request.Mapping, binding);

        var (fullWidth, fullHeight) = OnnxVolumeBinder.PlaneSize(volume, request.Plane);
        var window = request.Region is { } region
            ? new OnnxSpan(Math.Clamp(region.X0, 0, fullWidth - 1), Math.Clamp(region.X0 + region.Width, 1, fullWidth))
            : new OnnxSpan(0, fullWidth);
        var rows = request.Region is { } r
            ? new OnnxSpan(Math.Clamp(r.Y0, 0, fullHeight - 1), Math.Clamp(r.Y0 + r.Height, 1, fullHeight))
            : new OnnxSpan(0, fullHeight);

        var values = new float[rows.End - rows.Start, window.End - window.Start];
        var extent = entry.Info.SpatialExtent;

        // A model that accepts any size needs one call, so no overlap is planned; one with a fixed size is tiled and
        // gets the seam halo below.
        var plan = OnnxVolumeBinder.PlanTiles(
            window.End - window.Start,
            rows.End - rows.Start,
            1,
            extent,
            extent.IsTiled ? SliceHalo : 0);

        using var functor = OpenFunctor(entry);
        var stopwatch = Stopwatch.StartNew();

        foreach (var tile in plan.Tiles)
        {
            var data = OnnxVolumeBinder.ReadPlaneRegion(
                volume,
                request.Plane,
                request.Index,
                window.Start + tile.X0,
                rows.Start + tile.Y0,
                tile.Width,
                tile.Height,
                mapping);

            var output = functor.Query(data, [1, 1, tile.Height, tile.Width]);
            var tileValues = OnnxTensorReader.TryReadPlane(output)
                ?? throw new InvalidOperationException(
                    $"模型 '{entry.Info.Id}' 的输出为 {FormatShape(output)}，切片运行需要至少二维的浮点输出。");

            CopyTile(values, tileValues, tile);
        }

        stopwatch.Stop();
        var (min, max) = OnnxSliceResultRange(values);

        return new OnnxSliceResult
        {
            Plane = request.Plane,
            Index = request.Index,
            Values = values,
            Min = min,
            Max = max,
            Tiles = plan.Tiles.Count,
            ElapsedMs = stopwatch.Elapsed.TotalMilliseconds
        };
    }

    /// <summary>
    /// Copies a tile's output into the plane. A tile that ran past the plane's edge read clamped voxels, and the
    /// copies those produced are dropped here rather than written: they describe a border the user cannot see, and
    /// letting them in would make the preview disagree with an apply over the same window.
    /// </summary>
    private static void CopyTile(float[,] target, float[,] source, OnnxTile tile)
    {
        var baseX = tile.OwnX.Start - tile.X0;
        var baseY = tile.OwnY.Start - tile.Y0;
        var width = Math.Min(source.GetLength(1) - baseX, tile.OwnX.End - tile.OwnX.Start);
        var height = Math.Min(source.GetLength(0) - baseY, tile.OwnY.End - tile.OwnY.Start);

        for (var row = 0; row < height; row++)
        {
            var y = tile.OwnY.Start + row;
            if (y < 0 || y >= target.GetLength(0))
                continue;

            for (var column = 0; column < width; column++)
            {
                var x = tile.OwnX.Start + column;
                if (x < 0 || x >= target.GetLength(1))
                    continue;

                target[y, x] = source[baseY + row, baseX + column];
            }
        }
    }

    private static (float Min, float Max) OnnxSliceResultRange(float[,] values)
    {
        var min = float.PositiveInfinity;
        var max = float.NegativeInfinity;
        foreach (var value in values)
        {
            if (value < min) min = value;
            if (value > max) max = value;
        }
        return (float.IsInfinity(min) ? 0f : min, float.IsInfinity(max) ? 0f : max);
    }

    /// <summary>Halo a tiled slice read uses, so a fixed-size model's tiles agree on the seam between them.</summary>
    private const int SliceHalo = 2;
    /// <summary>
    /// Runs the model across the whole volume, one tile at a time.
    /// <para>
    /// The output range comes from the binding rather than from measuring the run. A band-pass model's range is a
    /// property of its graph, and measuring it would mean either a second inference pass over the whole volume or
    /// materialising a float volume of the data - both far worse than asking for the two numbers the panel already
    /// shows. A tile at the volume's edge is filled by clamping, which is exactly what the model would do with a
    /// zero-padded border and avoids a border discontinuity in the result.
    /// </para>
    /// </summary>
    public OnnxVolumeResult RunVolume(OnnxVolumeRequest request, CancellationToken cancellationToken)
    {
        var entry = Require(request.ModelId);
        var volume = RequireVolume();
        var binding = _settings.BindingFor(entry.Info);
        var mapping = ResolveMapping(request.Mapping, binding);

        var plan = OnnxVolumeBinder.PlanTiles(volume, entry.Info.SpatialExtent, request.TileOverlap);
        var outputMin = request.OutputMin ?? binding.OutputMin;
        var outputMax = request.OutputMax ?? binding.OutputMax;
        var scale = outputMax > outputMin ? 1.0 / (outputMax - outputMin) : 0.0;

        var target = Big3DArrayFactory.CreateEmpty(request.ElementType, volume.X, volume.Y, volume.Z);
        using var functor = OpenFunctor(entry);

        var stopwatch = Stopwatch.StartNew();
        foreach (var tile in plan.Tiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var block = OnnxVolumeBinder.ReadBlock(
                volume, tile.X0, tile.Y0, tile.Z0, tile.Width, tile.Height, tile.Depth, mapping);

            var shape = plan.HasDepthAxis
                ? new[] { 1, 1, tile.Depth, tile.Height, tile.Width }
                : new[] { 1, 1, tile.Height, tile.Width };

            OnnxVolumeWriter.WriteTile(
                target, request.ElementType, tile, functor.Query(block, shape), plan.HasDepthAxis, outputMin, scale);
        }

        stopwatch.Stop();
        return new OnnxVolumeResult
        {
            Volume = target,
            OutputMin = outputMin,
            OutputMax = outputMax,
            Tiles = plan.Tiles.Count,
            ElapsedMs = stopwatch.Elapsed.TotalMilliseconds
        };
    }

    /// <summary>Runs the model on a tensor the caller supplied, with no reference to the session's data.</summary>
    public OnnxRunResult RunRaw(OnnxRunRequest request)
    {
        var entry = Require(request.ModelId);
        if (request.Inputs.Count != 1)
        {
            throw new NotSupportedException(
                $"模型 '{entry.Info.Id}' 收到 {request.Inputs.Count} 个输入；本运行器一次只绑定一个，" +
                "因为第二个输入通常携带只有调用方才知道其含义的参数。");
        }

        var (name, tensor) = request.Inputs.First();
        var floats = OnnxTensorReader.AsFloatSpan(tensor).ToArray();
        var outputName = request.OutputNames.Count > 0 ? request.OutputNames[0] : null;

        using var functor = OpenFunctor(entry);
        var stopwatch = Stopwatch.StartNew();
        var output = functor.Query(floats, tensor.Shape, outputName);
        stopwatch.Stop();

        return new OnnxRunResult
        {
            Outputs = [output],
            ElapsedMs = stopwatch.Elapsed.TotalMilliseconds
        };
    }

    /// <summary>
    /// Takes a lease on the session and hands back a functor that releases it. The lease is what makes a rescan
    /// that drops a changed model safe while an inference is still running against the old session.
    /// </summary>
    private OnnxSessionFunctor OpenFunctor(OnnxModelEntry entry)
    {
        var lease = entry.Acquire()
            ?? throw new InvalidOperationException($"模型 '{entry.Info.Id}' 正在被替换，请重试。");
        return new OnnxSessionFunctor(entry.Session, entry.Info, lease);
    }

    /// <summary>
    /// A request's own mapping wins over the stored binding, so a caller can run one model two ways without
    /// changing the user's saved configuration.
    /// </summary>
    private static OnnxInputMapping ResolveMapping(OnnxInputMapping requested, OnnxBinding binding) =>
        requested == OnnxInputMapping.Identity ? binding.ToMapping() : requested;

    private OnnxModelEntry Require(string idOrNameOrPath) =>
        _store.Find(idOrNameOrPath)
        ?? throw new InvalidOperationException($"没有匹配 '{idOrNameOrPath}' 的 ONNX 模型。");

    private ReadonlyBig3DArray RequireVolume() =>
        _volumes.Current ?? throw new InvalidOperationException("没有加载体数据，无法运行模型。");

    private static string FormatShape(OnnxTensorData output) =>
        $"'{output.Name}' {OnnxDim.Format(output.Shape.Select(d => new OnnxDim(d)).ToList())}";
}
