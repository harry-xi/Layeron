using Layeron.Big3DArray;
using Numpy;
using Python.Runtime;

namespace Layeron.Plugins.PythonRunner;

/// <summary>
/// numpy marshalling for the script API.
/// <para>
/// Two facts shape everything here, both measured rather than assumed.
/// </para>
/// <para>
/// <b>A numpy array is never marshalled into a CLR array.</b> Handing one to a <c>bool[]</c> parameter raises
/// <c>TypeError: No method matches given arguments</c>, and the runtime offers no conversion to fall back on.
/// So an inbound array is read through Python's own <c>ravel().tolist()</c> and converted element by element.
/// That is slower than a buffer copy, and it is the only correct route - which is why it is written once and
/// shared rather than reinvented per call site.
/// </para>
/// <para>
/// <b>The dtype must always be stated.</b> The binding's generic overloads map CLR types through their own
/// table, which turns a <c>byte[]</c> into <c>int8</c> rather than <c>uint8</c> - so every array published
/// here passes its dtype explicitly, or an 8-bit volume would come back to Python as signed and a 16-bit one
/// as a different type than it is stored in.
/// </para>
/// </summary>
internal static class Arrays
{
    /// <summary>numpy dtype name of a volume's element type.</summary>
    public static string DtypeName(PixelElementType? elementType) => elementType switch
    {
        PixelElementType.Byte => "uint8",
        PixelElementType.UInt16 => "uint16",
        _ => "none"
    };

    /// <summary>Wraps a managed array as a numpy array of the given dtype.</summary>
    public static PyObject ToNumpy<T>(T[] values, Dtype dtype) => np.array(values, dtype: dtype).PyObject;

    public static PyObject ToNumpy<T>(T[,] values, Dtype dtype) => np.array(values, dtype: dtype).PyObject;

    public static PyObject ToNumpy<T>(T[,,] values, Dtype dtype) => np.array(values, dtype: dtype).PyObject;

    /// <summary>
    /// 16-bit voxels, widened to <c>int</c> before they are published.
    /// <para>
    /// Not <c>np.array(ushort[,], dtype: np.uint16)</c>, which is the obvious call and does not work: the
    /// binding maps a CLR <c>ushort</c> through its own table onto <c>Int16</c> and then casts element by
    /// element, so any voxel above 32767 throws <c>InvalidCastException</c> - measured on a volume full of
    /// 40000. Widening first sidesteps the table entirely: an <c>int</c> maps correctly, and numpy performs
    /// the narrowing into <c>uint16</c> itself, where it is defined.
    /// </para>
    /// <para>
    /// The cost is a transient 4-byte-per-voxel copy of whatever is being read. That is accepted deliberately:
    /// the alternative is a whole 16-bit read path that cannot return values over half its range.
    /// </para>
    /// </summary>
    public static PyObject ToNumpy(ushort[,] values, Dtype dtype)
    {
        var widened = new int[values.GetLength(0), values.GetLength(1)];

        for (var row = 0; row < widened.GetLength(0); row++)
        for (var column = 0; column < widened.GetLength(1); column++)
            widened[row, column] = values[row, column];

        return np.array(widened, dtype: dtype).PyObject;
    }

    /// <inheritdoc cref="ToNumpy(ushort[,], Dtype)"/>
    public static PyObject ToNumpy(ushort[,,] values, Dtype dtype)
    {
        var widened = new int[values.GetLength(0), values.GetLength(1), values.GetLength(2)];

        for (var z = 0; z < widened.GetLength(0); z++)
        for (var y = 0; y < widened.GetLength(1); y++)
        for (var x = 0; x < widened.GetLength(2); x++)
            widened[z, y, x] = values[z, y, x];

        return np.array(widened, dtype: dtype).PyObject;
    }

    /// <summary>
    /// Copies an array back into a managed bool array, and reports the shape it claimed.
    /// <para>
    /// The dtype is checked rather than trusted. Reading element by element would happily accept a float array
    /// and turn every nonzero value into true, which is the kind of silence that makes a wrong mask look like a
    /// working one. Rejecting the wrong dtype is what keeps such a mistake visible.
    /// </para>
    /// </summary>
    public static bool[] ReadBools(PyObject? source, out int[] shape)
    {
        ArgumentNullException.ThrowIfNull(source);

        shape = ReadShape(source);
        var kind = DtypeNameOf(source);

        return kind switch
        {
            "bool" => ReadFlat(source, shape, static item => item.As<bool>()),

            // As<long> rather than As<int>: the runtime's own mapping is unreliable for anything narrower than
            // 64 bits (see ReadVoxels), and a wide read is exact whatever the array's integer type is.
            "uint8" or "int8" or "uint16" or "int16" or "uint32" or "int32" or "uint64" or "int64" =>
                ReadFlat(source, shape, static item => item.As<long>() != 0),
            _ => throw new ArgumentException(
                $"A mask must be a numpy array of bool or of 0/1 integers, but this array is '{kind}'.",
                nameof(source))
        };
    }

    /// <summary>Reads voxels, choosing the storage depth from the array's integer dtype.</summary>
    public static (Array Values, int SizeZ, int SizeY, int SizeX) ReadVoxels(PyObject? source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var shape = ReadShape(source);
        if (shape.Length != 3)
        {
            throw new ArgumentException(
                $"A volume must be a 3-D array shaped [z, y, x], but this array has {shape.Length} dimension(s).",
                nameof(source));
        }

        var kind = DtypeNameOf(source);

        var values = kind switch
        {
            "bool" => (Array)ReadFlat(source, shape, static item => item.As<bool>() ? (byte)1 : (byte)0),

            // As<long> rather than As<byte>: an int8 array is documented as 8-bit storage, and the runtime
            // rejects a negative value when asked for a byte directly. Narrowing from a wide integer keeps
            // the two's-complement bit pattern, so -1 becomes 255 - the same rule the 16-bit path below
            // already follows. Unsigned values in 0..255 are unchanged.
            "uint8" or "int8" => ReadFlat(source, shape, static item => unchecked((byte)item.As<long>())),

            // Not As<ushort>(): the runtime's type table maps an unsigned 16-bit request onto Int16 and then
            // fails the cast on a value above 32767. Reading as a wide integer and narrowing is exact, and a
            // 16-bit voxel always fits.
            "uint16" or "int16" => ReadFlat(source, shape, static item => unchecked((ushort)item.As<long>())),
            _ => throw new ArgumentException(
                $"A volume must be uint8, uint16, int8, int16 or bool, but this array is '{kind}'.",
                nameof(source))
        };

        return (values, shape[0], shape[1], shape[2]);
    }

    /// <summary>The shape of an array, as a managed array of dimensions.</summary>
    public static int[] ReadShape(PyObject array)
    {
        using var shape = array.GetAttr("shape");
        var result = new int[(int)shape.Length()];

        for (var i = 0; i < result.Length; i++)
        {
            using var dimension = shape.GetItem(i);
            result[i] = dimension.As<int>();
        }

        return result;
    }

    private static string DtypeNameOf(PyObject array)
    {
        if (!array.HasAttr("dtype"))
            throw new ArgumentException("Expected a numpy array.", nameof(array));

        using var name = array.GetAttr("dtype").GetAttr("name");

        // A dtype always has a name; the fallback keeps the failure in the caller's message rather than here.
        return name.ToString() ?? "unknown";
    }

    /// <summary>
    /// Materialises an array's elements one at a time, in row-major order - the order the host stores voxels
    /// in, so index <c>i</c> of the result is voxel <c>i</c> of a flattened <c>[z, y, x]</c> array.
    /// </summary>
    private static T[] ReadFlat<T>(PyObject array, int[] shape, Func<PyObject, T> convert)
    {
        using var flat = array.InvokeMethod("ravel");
        using var list = new PyList(flat.InvokeMethod("tolist"));

        var total = Total(shape);
        var result = new T[total];

        for (var i = 0; i < total; i++)
        {
            using var item = list[i];
            result[i] = convert(item);
        }

        return result;
    }

    /// <summary>Reshapes a flat buffer into a 2-D array for the bitmap slice setters.</summary>
    public static bool[,] AsMatrix(bool[] flat, int[] shape)
    {
        var result = new bool[shape[0], shape[1]];

        for (var row = 0; row < shape[0]; row++)
        for (var column = 0; column < shape[1]; column++)
            result[row, column] = flat[row * shape[1] + column];

        return result;
    }

    private static int Total(int[] shape) =>
        shape.Aggregate(1, static (product, dimension) => product * dimension);
}

/// <summary>Builds the small tuples the API returns to Python.</summary>
internal static class Shapes
{
    /// <summary>A Python tuple of ints - what <c>shape</c> and <c>crosshair</c> return.</summary>
    public static PyTuple AsTuple(params int[] values) =>
        new([.. values.Select(static value => value.ToPython())]);

    /// <summary>A Python tuple of RGB values.</summary>
    public static PyTuple AsTuple(byte r, byte g, byte b) =>
        new([r.ToPython(), g.ToPython(), b.ToPython()]);

    /// <summary>A Python tuple of RGBA values; the alpha channel is a layer's opacity.</summary>
    public static PyTuple AsTuple(byte r, byte g, byte b, byte a) =>
        new([r.ToPython(), g.ToPython(), b.ToPython(), a.ToPython()]);

    /// <summary>Reshapes a flat bool buffer into the 2-D array the bitmap slice setters take.</summary>
    public static bool[,] AsMatrix(bool[] flat, int[] shape) => Arrays.AsMatrix(flat, shape);
}
