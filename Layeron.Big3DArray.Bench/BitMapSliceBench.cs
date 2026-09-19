using BenchmarkDotNet.Attributes;

namespace Layeron.Big3DArray.Bench;

/// <summary>
/// Performance measurement for the slice accessors added to <see cref="Big3DBitMap"/>
/// (mirroring <see cref="Big3DArray"/>).
///
/// The cost of slicing scales with the slice AREA (Size^2), not the volume:
///   * Size=256  ->  256x256   plane =       65,536 reads.
///   * Size=1024 -> 1024x1024  plane =    1,048,576 reads.
///   * Size=4096 -> 4096x4096  plane =   16,777,216 reads.
/// So larger rows inherently do more work; compare orientations within the same
/// Size, not across Sizes.
/// </summary>
[MemoryDiagnoser]
public class BitMapSliceBench
{
    [Params(256, 1024, 4096)]
    public int Size { get; set; }

    private const int FillEdge = 256;
    private static readonly double NoiseFrequency = 1.0 / 32.0;

    private Big3DBitMap? _map;

    [GlobalSetup]
    public void Setup()
    {
        _map = new Big3DBitMap(Size, Size, Size);

        // Populate a smooth Perlin-noise region so slices read real blocks.
        var rnd = new Random(999);
        for (var z = 0; z < FillEdge; z++)
        for (var y = 0; y < FillEdge; y++)
        for (var x = 0; x < FillEdge; x++)
            if (PerlinNoise.Sample(x, y, z, NoiseFrequency) > 0.0)
                _map[x, y, z] = true;

        // A few scattered points elsewhere for realism on the larger plane.
        for (var i = 0; i < 1000; i++)
            _map[rnd.Next(Size), rnd.Next(Size), rnd.Next(Size)] = true;
    }

    [Benchmark]
    public bool[,] GetXYSlice() => _map!.GetXYSlice(Size / 2);

    [Benchmark]
    public bool[,] GetXZSlice() => _map!.GetXZSlice(Size / 2);

    [Benchmark]
    public bool[,] GetYZSlice() => _map!.GetYZSlice(Size / 2);
}
