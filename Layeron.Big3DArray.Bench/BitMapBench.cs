using BenchmarkDotNet.Attributes;
using Layeron.Big3DArray;

namespace Layeron.Big3DArray.Bench;

/// <summary>
/// Perlin-noise-fill benchmark for <see cref="Big3DBitMap"/>, covering every
/// operation across sizes and fill ratios.
///
/// Dimensions:
///   * Size        - 1024 and 4096.
///   * FillRatio   - how much of the 256^3 data volume is populated (0.25/0.75).
///   * Operations  - random get, random set, bulk fill+read, and an XY slice.
///
/// Occupancy is generated with 3D Perlin noise (smooth, continuous features)
/// plus a small noise overlay. Random coordinates are pre-generated so the timed
/// region excludes generators; results are accumulated to defeat JIT elimination.
/// </summary>
[MemoryDiagnoser]
public class BitMapBench
{
    [Params(1024, 4096)]
    public int Size { get; set; }

    [Params(0.25, 0.75)]
    public double FillRatio { get; set; }

    private const int OpCount = 1_000_000;
    private const int FillEdge = 256;

    private static readonly double NoiseFrequency = 1.0 / 32.0;
    private static readonly double NoiseJitter = 0.02;

    private Big3DBitMap? _map;
    private bool[]? _voxels;
    private (int x, int y, int z)[]? _randomPoints;
    private (int x, int y, int z, bool v)[]? _randomOps;

    [GlobalSetup]
    public void Setup()
    {
        _voxels = BuildNoiseVolume();
        _map = new Big3DBitMap(Size, Size, Size);

        for (var z = 0; z < FillEdge; z++)
        for (var y = 0; y < FillEdge; y++)
        for (var x = 0; x < FillEdge; x++)
            if (_voxels[z * FillEdge * FillEdge + y * FillEdge + x])
                _map[x, y, z] = true;

        var rnd = new Random(12345);
        _randomPoints = new (int, int, int)[OpCount];
        _randomOps = new (int, int, int, bool)[OpCount];
        for (var i = 0; i < OpCount; i++)
        {
            var x = rnd.Next(FillEdge);
            var y = rnd.Next(FillEdge);
            var z = rnd.Next(FillEdge);
            _randomPoints[i] = (x, y, z);
            _randomOps[i] = (x, y, z, rnd.Next(2) == 0);
        }
    }

    private bool[] BuildNoiseVolume()
    {
        var total = FillEdge * FillEdge * FillEdge;
        var field = new double[total];
        var rnd = new Random(424242);

        for (var z = 0; z < FillEdge; z++)
        for (var y = 0; y < FillEdge; y++)
        for (var x = 0; x < FillEdge; x++)
        {
            var n = PerlinNoise.Sample(x, y, z, NoiseFrequency);
            field[z * FillEdge * FillEdge + y * FillEdge + x] = n + (rnd.NextDouble() * 2 - 1) * NoiseJitter;
        }

        const int bins = 200;
        var hist = new int[bins];
        for (var i = 0; i < total; i++)
        {
            var b = (int)Math.Floor((field[i] + 1) * 0.5 * bins);
            b = Math.Clamp(b, 0, bins - 1);
            hist[b]++;
        }

        var target = (long)(total * FillRatio);
        var cumulative = 0L;
        var thresholdBin = bins - 1;
        for (var b = bins - 1; b >= 0; b--)
        {
            cumulative += hist[b];
            if (cumulative >= target)
            {
                thresholdBin = b;
                break;
            }
        }

        var threshold = -1.0 + (thresholdBin + 0.5) * (2.0 / bins);

        var voxels = new bool[total];
        for (var i = 0; i < total; i++)
            voxels[i] = field[i] >= threshold;
        return voxels;
    }

    [Benchmark]
    public bool Get_Random()
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
    public bool Get_Sequential()
    {
        var m = _map!;
        var n = FillEdge;
        var acc = false;
        for (var z = 0; z < n; z++)
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
            acc ^= m[x, y, z];
        return acc;
    }

    [Benchmark]
    public bool Set_Random()
    {
        var m = _map!;
        var ops = _randomOps!;
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
    public bool Fill_Then_ReadBack()
    {
        var m = _map!;
        var n = FillEdge;
        for (var z = 0; z < n; z++)
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
            m[x, y, z] = true;

        var acc = false;
        for (var z = 0; z < n; z++)
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
            acc ^= m[x, y, z];
        return acc;
    }

    [Benchmark]
    public bool[,] GetXYSlice()
    {
        var idx = Math.Min(Size / 2, FillEdge - 1);
        return _map!.GetXYSlice(idx);
    }
}
