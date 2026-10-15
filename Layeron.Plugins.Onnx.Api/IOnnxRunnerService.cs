using Layeron.Big3DArray;

namespace Layeron.Plugins.Onnx.Api;

/// <summary>
/// A model the runner can execute, described entirely without ONNX Runtime types.
/// <para>
/// The signature is read from the graph once, when the model is opened, and then travels with this
/// object: a caller decides how to bind its data by looking at the axis counts here, rather than by
/// knowing the model in advance.
/// </para>
/// </summary>
public sealed class OnnxModelInfo
{
    /// <summary>Stable id, unique within the runner: the model's file name without extension.</summary>
    public required string Id { get; init; }

    /// <summary>Display name: the model's own <c>name</c> metadata when present, else the file name.</summary>
    public required string Name { get; init; }

    /// <summary>Free-form description from the model's metadata, when present.</summary>
    public string? Description { get; init; }

    /// <summary>Full path of the model file on disk.</summary>
    public required string Path { get; init; }

    /// <summary>Inputs, in graph order.</summary>
    public required IReadOnlyList<OnnxTensorSpec> Inputs { get; init; }

    /// <summary>Outputs, in graph order.</summary>
    public required IReadOnlyList<OnnxTensorSpec> Outputs { get; init; }

    /// <summary>Execution providers the session was created with, e.g. <c>["CPU"]</c>.</summary>
    public required IReadOnlyList<string> Providers { get; init; }

    /// <summary>Size of the model file in bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>
    /// Spatial size the model insists on, derived from the input whose axes look like
    /// channels-height-width (with an optional depth). A rank-2 input yields
    /// <c>Depth == 0</c>; a rank-3 input yields <c>Depth &lt;= 0</c>. All zero means the model
    /// accepts any size and the runner maps a region in one pass.
    /// </summary>
    public OnnxSpatialExtent SpatialExtent { get; init; }

    /// <summary>Primary input, i.e. the one data is bound to. Null when the model has no inputs.</summary>
    public OnnxTensorSpec? PrimaryInput => Inputs.Count > 0 ? Inputs[0] : null;

    /// <summary>Primary output, i.e. the tensor results are read from. Null when the model has no outputs.</summary>
    public OnnxTensorSpec? PrimaryOutput => Outputs.Count > 0 ? Outputs[0] : null;

    public override string ToString() =>
        $"{Name} ({string.Join(" -> ", Inputs.Select(i => OnnxDim.Format(i.Shape)))} => " +
        $"{string.Join(" / ", Outputs.Select(o => OnnxDim.Format(o.Shape)))})";
}

/// <summary>How a region of the session's volume is scaled onto a model input.</summary>
public sealed record OnnxInputMapping
{
    /// <summary>Low end of the source range, i.e. the voxel value that maps to <see cref="TargetMin"/>.</summary>
    public float SourceMin { get; init; }

    /// <summary>High end of the source range.</summary>
    public float SourceMax { get; init; } = 1f;

    /// <summary>Value the low end maps to.</summary>
    public float TargetMin { get; init; }

    /// <summary>Value the high end maps to.</summary>
    public float TargetMax { get; init; } = 1f;

    /// <summary>
    /// Identity mapping, used when a request does not describe one. Deliberately not a
    /// volume-derived default: the caller knows whether its data is 0..255 or 0..4095, and a
    /// silently invented range would make every result subtly wrong.
    /// </summary>
    public static OnnxInputMapping Identity { get; } = new();

    /// <summary>Linear map from the source range onto <c>[0,1]</c>, the common case for a uint8 or uint16 volume.</summary>
    public static OnnxInputMapping FromRange(long min, long max) => new()
    {
        SourceMin = min,
        SourceMax = max == min ? min + 1 : max,
        TargetMin = 0f,
        TargetMax = 1f
    };

    /// <summary>Applies the mapping to one value.</summary>
    public float Apply(float value)
    {
        var span = SourceMax - SourceMin;
        if (span == 0f)
            return TargetMin;
        return TargetMin + (value - SourceMin) / span * (TargetMax - TargetMin);
    }
}

/// <summary>
/// Plane a slice run reads from. Deliberately this plugin's own enum rather than
/// <c>SlicePlane</c>: the contract is compiled against by other plugins, and a duplicated two-value
/// concept here costs less than a contract that cannot be read without the plugin abstractions.
/// </summary>
public enum OnnxSlicePlane
{
    XY = 0,
    XZ = 1,
    YZ = 2
}

/// <summary>Runs a model over one plane of the session's current volume.</summary>
public sealed class OnnxSliceRequest
{
    /// <summary>Model id as reported by <see cref="OnnxModelInfo.Id"/>.</summary>
    public required string ModelId { get; init; }

    /// <summary>Plane to read.</summary>
    public required OnnxSlicePlane Plane { get; init; }

    /// <summary>Slice index inside the plane. Ignored when the model accepts any size and no region is given.</summary>
    public int Index { get; init; }

    /// <summary>Optional sub-rectangle of the plane; the whole plane when omitted.</summary>
    public OnnxRegion? Region { get; init; }

    /// <summary>Voxel-to-input scaling. Identity when omitted.</summary>
    public OnnxInputMapping Mapping { get; init; } = OnnxInputMapping.Identity;

    /// <summary>Name of the input to bind to; the model's primary input when omitted.</summary>
    public string? InputName { get; init; }
}

/// <summary>A half-open rectangle inside a plane, in the plane's own axis order.</summary>
public readonly record struct OnnxRegion(int X0, int Y0, int Width, int Height);

/// <summary>Result of a slice run: the plane's output, assembled in the plane's own coordinates.</summary>
public sealed class OnnxSliceResult
{
    /// <summary>Plane the input was read from, so a caller can publish the overlay on the right view.</summary>
    public required OnnxSlicePlane Plane { get; init; }

    /// <summary>Index the input was read at.</summary>
    public required int Index { get; init; }

    /// <summary>
    /// Output values over the whole plane, laid out <c>[row, column]</c> in the plane's own coordinates. A model
    /// with a fixed input size is run per tile and stitched back into this one buffer, so a caller never learns how
    /// many inference calls it took.
    /// </summary>
    public required float[,] Values { get; init; }

    /// <summary>Smallest output value over the plane, computed once here rather than by every caller.</summary>
    public required float Min { get; init; }

    /// <summary>Largest output value over the plane.</summary>
    public required float Max { get; init; }

    /// <summary>Number of inference calls the plane took: one when the model accepts any size, more when it does not.</summary>
    public int Tiles { get; init; } = 1;

    /// <summary>Wall-clock time of the whole plane in milliseconds.</summary>
    public double ElapsedMs { get; init; }
}

/// <summary>Runs a model across the whole volume, tiled along the axes the model leaves open.</summary>
public sealed class OnnxVolumeRequest
{
    /// <summary>Model id as reported by <see cref="OnnxModelInfo.Id"/>.</summary>
    public required string ModelId { get; init; }

    /// <summary>
    /// Element type of the produced volume. <see cref="PixelElementType.UInt16"/> keeps more of the
    /// model's output range and is the default for a float model.
    /// </summary>
    public PixelElementType ElementType { get; init; } = PixelElementType.UInt16;

    /// <summary>
    /// Output range mapped onto the element type's full range - the whole signal is scaled to fit
    /// rather than clipped. Both null means derive the range from the actual output.
    /// </summary>
    public float? OutputMin { get; init; }

    /// <summary>High end of the output range; see <see cref="OutputMin"/>.</summary>
    public float? OutputMax { get; init; }

    /// <summary>Voxel-to-input scaling. Identity when omitted.</summary>
    public OnnxInputMapping Mapping { get; init; } = OnnxInputMapping.Identity;

    /// <summary>Name of the input to bind to; the model's primary input when omitted.</summary>
    public string? InputName { get; init; }

    /// <summary>Overlap between neighbouring tiles in voxels, along each open axis. Ignored when the model fixes its size.</summary>
    public int TileOverlap { get; init; } = 4;
}

/// <summary>Result of a whole-volume run.</summary>
public sealed class OnnxVolumeResult
{
    /// <summary>The produced volume. Dimensions match the source volume; never null.</summary>
    public required ReadonlyBig3DArray Volume { get; init; }

    /// <summary>Low end of the output range the voxels were scaled from - the model's own minimum.</summary>
    public required float OutputMin { get; init; }

    /// <summary>High end of the output range the voxels were scaled from.</summary>
    public required float OutputMax { get; init; }

    /// <summary>Number of inference calls it took, i.e. the number of tiles.</summary>
    public int Tiles { get; init; }

    /// <summary>Wall-clock time of the whole run in milliseconds.</summary>
    public double ElapsedMs { get; init; }
}

/// <summary>What the caller supplies for a raw tensor run.</summary>
public sealed class OnnxRunRequest
{
    /// <summary>Model id as reported by <see cref="OnnxModelInfo.Id"/>.</summary>
    public required string ModelId { get; init; }

    /// <summary>Inputs by tensor name. Every non-optional model input must be present.</summary>
    public required IReadOnlyDictionary<string, OnnxTensorData> Inputs { get; init; }

    /// <summary>Output names to compute. Empty means every graph output.</summary>
    public IReadOnlyList<string> OutputNames { get; init; } = [];
}

/// <summary>Result of a raw tensor run.</summary>
public sealed class OnnxRunResult
{
    /// <summary>Requested outputs, in the order the graph declares them.</summary>
    public required IReadOnlyList<OnnxTensorData> Outputs { get; init; }

    /// <summary>Wall-clock time of the inference call in milliseconds.</summary>
    public double ElapsedMs { get; init; }

    /// <summary>First output, or null when the graph produced none.</summary>
    public OnnxTensorData? Primary => Outputs.Count > 0 ? Outputs[0] : null;
}

/// <summary>
/// The ONNX runner, published by the runtime plugin so that any other plugin can execute a model.
/// <para>
/// Every method is safe to call from any thread and from several threads at once: calls queue onto the
/// session's own worker, exactly as the Python runner's do. The methods block that worker, not the
/// caller's thread, so a UI caller must still use the <c>Async</c> form rather than waiting.
/// </para>
/// <para>
/// The point this contract is published under belongs to the runner plugin, so unloading it removes the point as
/// well as the contribution: a consumer resolves the service through
/// <c>IPluginRegistry.GetPoint&lt;IOnnxRunnerService&gt;</c> and must handle a null result, which is what
/// "the runner is not installed or not loaded" looks like from the outside.
/// </para>
/// </summary>
public interface IOnnxRunnerService
{
    /// <summary>Extension point id the service is contributed to.</summary>
    public const string PointId = "layeron.onnx.runner";

    /// <summary>Models currently known to the runner, in load order.</summary>
    IReadOnlyList<OnnxModelInfo> Models { get; }

    /// <summary>Raised when <see cref="Models"/> changes, e.g. after a rescan.</summary>
    event Action? ModelsChanged;

    /// <summary>Rescans the model folders. Returns the number of models added or removed.</summary>
    Task<int> RescanAsync(CancellationToken cancellationToken = default);

    /// <summary>Finds a model by id, name, or file path; null when there is no match.</summary>
    OnnxModelInfo? GetModel(string idOrNameOrPath);

    /// <summary>Runs a model over one plane of the session's current volume.</summary>
    Task<OnnxSliceResult> RunSliceAsync(OnnxSliceRequest request, CancellationToken cancellationToken = default);

    /// <summary>Runs a model across the whole volume and returns the produced volume.</summary>
    Task<OnnxVolumeResult> RunVolumeAsync(OnnxVolumeRequest request, CancellationToken cancellationToken = default);

    /// <summary>Runs a model on tensors the caller supplied, with no reference to the session's data.</summary>
    Task<OnnxRunResult> RunAsync(OnnxRunRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads a flat tensor back into the shapes a caller actually wants. Lives in the contract because
/// both the panel and any consuming plugin need it, and because doing it twice by hand is how the two
/// copies end up disagreeing.
/// </summary>
public static class OnnxTensorReader
{
    /// <summary>
    /// Reads a <c>[height, width]</c> plane out of a tensor whose trailing axes are the plane and whose
    /// leading axes are taken at their first index - the shape a segmentation or denoising model emits.
    /// Returns null when the tensor is not at least rank 2 or does not hold floats.
    /// </summary>
    public static float[,]? TryReadPlane(OnnxTensorData? tensor)
    {
        if (tensor is null || tensor.Shape.Length < 2 || tensor.ElementKind != OnnxElementKind.Float)
            return null;

        var data = (float[])tensor.Data;
        var shape = tensor.Shape;
        var height = shape[^2];
        var width = shape[^1];
        if (height <= 0 || width <= 0)
            return null;

        var plane = new float[height, width];
        var stride = 1;
        for (var i = 0; i < shape.Length - 2; i++)
            stride *= shape[i];

        // Leading axes are dropped by taking index 0 on each of them, which is the whole tensor when
        // there are none (a bare [H,W] output) and the first channel/batch element otherwise.
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                plane[y, x] = data[y * width + x];

        return plane;
    }

    /// <summary>
    /// Views a tensor's contents as floats. Throws for a tensor that is not single-precision, because
    /// what a caller should do with a <c>bool</c> or <c>int64</c> output is model-specific.
    /// </summary>
    public static ReadOnlySpan<float> AsFloatSpan(OnnxTensorData tensor)
    {
        ArgumentNullException.ThrowIfNull(tensor);
        if (tensor.ElementKind != OnnxElementKind.Float || tensor.Data is not float[] floats)
            throw new InvalidOperationException(
                $"Tensor '{tensor.Name}' holds {tensor.ElementKind}; expected Float.");
        return floats;
    }
}
