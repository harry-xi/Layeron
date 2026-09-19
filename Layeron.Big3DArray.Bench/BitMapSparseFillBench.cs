using BenchmarkDotNet.Attributes;

namespace Layeron.Big3DArray.Bench;

/// <summary>
/// Benchmarks <see cref="Big3DBitMap"/> on maps that hold only a few small,
/// localized, continuous filled islands (like isolated terrain blobs) with NO
/// random noise scattered across the volume. This isolates the cost of reads and
/// writes for a large but very sparsely-occupied map.
///
/// Covered here:
///   * Size    - 1024 and 2048 (the software's expected practical upper bound).
///   * Ops     - read (island-local), read (whole volume), write (island-local), slice.
/// </summary>
[MemoryDiagnoser]
public class BitMapSparseFillBench
{
    [Params(1024, 2048)]
    public int Size { get; set; }

    private const int OpCount = 1_000_000;

    // Islands scaled relative to Size so both 1024 and 2048 have comparable sparsity.
    private (int x, int y, int z, int edge)[] IslandSpecs =>
        new (int, int, int, int)[]
        {
            (Size / 16, Size / 8, Size / 16, Size / 20),
            (Size / 2, Size / 8, Size / 3, Size / 30),
            (Size / 4, Size / 2, Size / 20, Size / 40),
            (Size * 3 / 4, Size * 3 / 4, Size / 3, Size / 15),
        };

    private Big3DBitMap? _map;
    private (int x, int y, int z)[]? _islandPoints;
    private (int x, int y, int z)[]? _randomPoints;
    private (int x, int y, int z, bool v)[]? _islandOps;
    private int _sliceZ;

    [GlobalSetup]
    public void Setup()
    {
        _map = new Big3DBitMap(Size, Size, Size);
        var islands = IslandSpecs;
        foreach (var (ix, iy, iz, edge) in islands)
        for (var z = 0; z < edge; z++)
        for (var y = 0; y < edge; y++)
        for (var x = 0; x < edge; x++)
            _map[ix + x, iy + y, iz + z] = true;

        var rnd = new Random(31337);

        // Points strictly inside the islands -> every read/write hits real data.
        var totalIslandVolume = 0L;
        foreach (var (_, _, _, edge) in islands) totalIslandVolume += (long)edge * edge * edge;
        _islandPoints = new (int, int, int)[OpCount];
        _islandOps = new (int, int, int, bool)[OpCount];
        for (var i = 0; i < OpCount; i++)
        {
            // Pick a random island weighted by its volume, then a random point in it.
            var pick = rnd.NextDouble() * totalIslandVolume;
            var acc = 0L;
            var chosen = islands[0];
            for (var j = 0; j < islands.Length; j++)
            {
                acc += (long)islands[j].edge * islands[j].edge * islands[j].edge;
                if (pick < acc) { chosen = islands[j]; break; }
            }

            var (ix, iy, iz, edge) = chosen;
            var x = ix + rnd.Next(edge);
            var y = iy + rnd.Next(edge);
            var z = iz + rnd.Next(edge);
            _islandPoints[i] = (x, y, z);
            _islandOps[i] = (x, y, z, rnd.Next(2) == 0);
        }

        // Random points across the whole volume (mostly empty; some hits in islands).
        _randomPoints = new (int, int, int)[OpCount];
        for (var i = 0; i < OpCount; i++)
            _randomPoints[i] = (rnd.Next(Size), rnd.Next(Size), rnd.Next(Size));

        // Slices across the first (largest) island.
        _sliceZ = islands[0].z + islands[0].edge / 2;
    }

    [Benchmark(Baseline = true)]
    public bool Get_Islands()
    {
        var m = _map!;
        var pts = _islandPoints!;
        var acc = false;
        for (var i = 0; i < pts.Length; i++)
        {
            var (x, y, z) = pts[i];
            acc ^= m[x, y, z];
        }
        return acc;
    }

    [Benchmark]
    public bool Get_RandomWholeVolume()
    {
        var m = _map!;
        var pts = _randomPoints!;
        var acc = false;
        for (var i = 0; i < pts.Length; i++)
        {
            var (x, y, z) = pts[i];
            acc ^= m[x, y, z];
        }
        return acc;
    }

    [Benchmark]
    public bool Set_Islands()
    {
        var m = _map!;
        var ops = _islandOps!;
        var acc = false;
        for (var i = 0; i < ops.Length; i++)
        {
            var (x, y, z, v) = ops[i];
            m[x, y, z] = v;
            acc ^= m[x, y, z];
        }
        return acc;
    }

    [Benchmark]
    public bool[,] GetXYSlice()
    {
        return _map!.GetXYSlice(_sliceZ);
    }
}
