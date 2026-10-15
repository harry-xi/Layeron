namespace Layeron.Plugins.Onnx.Api;

/// <summary>
/// Element type of a tensor. The numeric values are ONNX's own <c>TensorProto.DataType</c> values, so
/// the runtime converts by cast rather than by a mapping table that can fall out of step.
/// </summary>
public enum OnnxElementKind
{
    Unknown = 0,
    Float = 1,
    UInt8 = 2,
    Int8 = 3,
    UInt16 = 4,
    Int16 = 5,
    Int32 = 6,
    Int64 = 7,
    Bool = 9,
    Float16 = 10,
    Double = 11
}

/// <summary>One dimension of a tensor signature. A non-positive size means the model accepts any size.</summary>
public readonly record struct OnnxDim(int Size)
{
    /// <summary>True when the model does not fix this dimension.</summary>
    public bool IsDynamic => Size <= 0;

    public override string ToString() => IsDynamic ? "?" : Size.ToString();

    /// <summary>Renders a whole shape as <c>[1,1,32,32]</c>.</summary>
    public static string Format(IReadOnlyList<OnnxDim> shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (shape.Count == 0)
            return "[]";

        var parts = new string[shape.Count];
        for (var i = 0; i < shape.Count; i++)
            parts[i] = shape[i].ToString();
        return "[" + string.Join(",", parts) + "]";
    }
}

/// <summary>
/// The signature of one model input or output. This is what a caller binds against: how many axes the tensor has,
/// which of them the model fixes, and what element type it takes.
/// </summary>
public sealed class OnnxTensorSpec
{
    /// <summary>Tensor name as it appears in the graph; the key a run request names.</summary>
    public required string Name { get; init; }

    /// <summary>Element type.</summary>
    public required OnnxElementKind ElementKind { get; init; }

    /// <summary>Declared shape, one entry per axis, outermost first. May contain dynamic dimensions.</summary>
    public required IReadOnlyList<OnnxDim> Shape { get; init; }

    public override string ToString() => $"{Name} {OnnxDim.Format(Shape)} {ElementKind}";
}

/// <summary>
/// A tensor crossing the contract boundary: plain array plus shape, never an ORT type.
/// <para>
/// <see cref="Data"/> is flat and row-major, so its length is the product of <see cref="Shape"/> and a
/// multi-dimensional tensor is a plain <see cref="Array"/> rather than a <c>T[,]</c> - one
/// representation for every rank, which keeps a rank-5 volume output from becoming a special case.
/// </para>
/// </summary>
public sealed class OnnxTensorData
{
    /// <summary>Tensor name; for an output this is the graph's output name.</summary>
    public required string Name { get; init; }

    /// <summary>Element type, matching the type of <see cref="Data"/>.</summary>
    public required OnnxElementKind ElementKind { get; init; }

    /// <summary>Dimensions, outermost first.</summary>
    public required int[] Shape { get; init; }

    /// <summary>Flat row-major contents, length equal to the product of <see cref="Shape"/>.</summary>
    public required Array Data { get; init; }

    /// <summary>Number of elements, for a caller that needs it before touching the data.</summary>
    public int Length
    {
        get
        {
            var length = 1;
            foreach (var dim in Shape)
                length *= dim;
            return length;
        }
    }

    public override string ToString() => $"{Name} [{string.Join(",", Shape)}] {ElementKind}";
}

/// <summary>
/// Fixed spatial extent a model requires, in <c>(width, height, depth)</c> order. A non-positive value
/// means the model accepts any size on that axis, and the runner then maps the whole region in one pass
/// instead of tiling.
/// </summary>
public readonly record struct OnnxSpatialExtent(int Width, int Height, int Depth)
{
    /// <summary>True when the model fixes at least one spatial axis and therefore needs tiling.</summary>
    public bool IsTiled => Width > 0 || Height > 0 || Depth > 0;
}
