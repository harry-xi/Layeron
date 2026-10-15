using Layeron.Plugins.Onnx.Api;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Layeron.Plugins.Onnx.Runtime;

/// <summary>
/// Translates between the contract's plain tensor description and ONNX Runtime's own, in both directions
/// and in one place.
/// <para>
/// Every ORT type in this plugin funnels through here. That is the point: the contract assembly must never
/// see an ORT type - two copies of one would be two different types, and the published service would become
/// unreachable with nothing to explain why. One boundary file holds by construction what would otherwise be
/// a rule to remember at every call site.
/// </para>
/// </summary>
internal static class OnnxTensorAdapter
{
    /// <summary>
    /// Maps a node's metadata onto a contract spec. ONNX Runtime reports a dimension the model leaves open as
    /// <c>-1</c>, which lands on <see cref="OnnxDim.IsDynamic"/> directly - there is no translation table
    /// that could disagree with the graph.
    /// </summary>
    public static OnnxTensorSpec ToSpec(string name, NodeMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var dimensions = metadata.Dimensions;
        var shape = new OnnxDim[dimensions.Length];
        for (var i = 0; i < dimensions.Length; i++)
            shape[i] = new OnnxDim(dimensions[i]);

        return new OnnxTensorSpec
        {
            Name = name,
            ElementKind = (OnnxElementKind)(int)metadata.ElementDataType,
            Shape = shape
        };
    }

    /// <summary>
    /// Reads the spatial extent a model insists on out of its primary input's shape.
    /// <para>
    /// The reading is axis-count based, which is what a caller can act on without knowing the model:
    /// <c>[N,C,H,W]</c> fixes height and width, <c>[N,C,D,H,W]</c> fixes all three, and a shallower input
    /// fixes nothing. Anything longer than five axes is not treated as spatial, because a model that unusual
    /// needs a caller who already knows what it is.
    /// </para>
    /// <para>
    /// Zero and "fixed" are different answers and each does real work: an open axis means the region is
    /// mapped in one pass, while a fixed extent is what makes a whole-volume run tile.
    /// </para>
    /// </summary>
    public static OnnxSpatialExtent InferSpatialExtent(OnnxTensorSpec? input)
    {
        if (input is null)
            return default;

        var shape = input.Shape;
        return shape.Count switch
        {
            4 => new OnnxSpatialExtent(Normalize(shape[3]), Normalize(shape[2]), 0),
            5 => new OnnxSpatialExtent(Normalize(shape[4]), Normalize(shape[3]), Normalize(shape[2])),
            _ => default
        };

        static int Normalize(OnnxDim dim) => dim.IsDynamic ? 0 : dim.Size;
    }

    /// <summary>Wraps a flat array and its shape as a dense tensor ORT can bind.</summary>
    public static DenseTensor<T> ToDense<T>(T[] data, int[] shape) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(shape);

        var expected = 1;
        foreach (var dim in shape)
            expected *= dim;
        if (expected != data.Length)
        {
            throw new ArgumentException(
                $"Shape [{string.Join(",", shape)}] needs {expected} elements; got {data.Length}.",
                nameof(data));
        }

        return new DenseTensor<T>(data, shape);
    }

    /// <summary>Reads an ORT output back into a contract tensor.</summary>
    public static OnnxTensorData FromTensor<T>(string name, Tensor<T> tensor) where T : unmanaged => new()
    {
        Name = name,
        ElementKind = KindOf<T>(),
        Shape = tensor.Dimensions.ToArray(),
        Data = tensor.ToArray()
    };

    /// <summary>The contract kind of a CLR element type.</summary>
    public static OnnxElementKind KindOf<T>() where T : unmanaged => typeof(T) switch
    {
        var t when t == typeof(float) => OnnxElementKind.Float,
        var t when t == typeof(double) => OnnxElementKind.Double,
        var t when t == typeof(byte) => OnnxElementKind.UInt8,
        var t when t == typeof(sbyte) => OnnxElementKind.Int8,
        var t when t == typeof(short) => OnnxElementKind.Int16,
        var t when t == typeof(ushort) => OnnxElementKind.UInt16,
        var t when t == typeof(int) => OnnxElementKind.Int32,
        var t when t == typeof(long) => OnnxElementKind.Int64,
        var t when t == typeof(bool) => OnnxElementKind.Bool,
        _ => OnnxElementKind.Unknown
    };
}
