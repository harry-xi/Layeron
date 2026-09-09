namespace Layeron.Big3DArray.Bench;

/// <summary>
/// Minimal deterministic 3D Perlin noise used to generate smooth, continuous
/// voxel regions in the benchmark (mimicking terrain/lab data) rather than
/// random scatter.
/// </summary>
internal static class PerlinNoise
{
    private static readonly int[] Perm = CreatePermutation(12345);
    private const int PermMask = 255;

    private static int[] CreatePermutation(int seed)
    {
        var rnd = new Random(seed);
        var p = Enumerable.Range(0, 256).ToArray();
        for (var i = 255; i > 0; i--)
        {
            var j = rnd.Next(i + 1);
            (p[i], p[j]) = (p[j], p[i]);
        }

        // Duplicate to avoid wrapping checks.
        return p.Concat(p).ToArray();
    }

    /// <summary>Sample Perlin noise at (x, y, z) with the given spatial frequency. Result in [-1, 1].</summary>
    public static double Sample(double x, double y, double z, double frequency = 1.0)
    {
        x *= frequency;
        y *= frequency;
        z *= frequency;

        var xi = (int)Math.Floor(x) & PermMask;
        var yi = (int)Math.Floor(y) & PermMask;
        var zi = (int)Math.Floor(z) & PermMask;

        var xf = x - Math.Floor(x);
        var yf = y - Math.Floor(y);
        var zf = z - Math.Floor(z);

        var u = Fade(xf);
        var v = Fade(yf);
        var w = Fade(zf);

        var aaa = Perm[Perm[Perm[xi] + yi] + zi];
        var aba = Perm[Perm[Perm[xi] + yi + 1] + zi];
        var aab = Perm[Perm[Perm[xi] + yi] + zi + 1];
        var abb = Perm[Perm[Perm[xi] + yi + 1] + zi + 1];
        var baa = Perm[Perm[Perm[xi + 1] + yi] + zi];
        var bba = Perm[Perm[Perm[xi + 1] + yi + 1] + zi];
        var bab = Perm[Perm[Perm[xi + 1] + yi] + zi + 1];
        var bbb = Perm[Perm[Perm[xi + 1] + yi + 1] + zi + 1];

        var x1 = Lerp(Grad(aaa, xf, yf, zf), Grad(baa, xf - 1, yf, zf), u);
        var x2 = Lerp(Grad(aba, xf, yf - 1, zf), Grad(bba, xf - 1, yf - 1, zf), u);
        var y1 = Lerp(x1, x2, v);

        var x3 = Lerp(Grad(aab, xf, yf, zf - 1), Grad(bab, xf - 1, yf, zf - 1), u);
        var x4 = Lerp(Grad(abb, xf, yf - 1, zf - 1), Grad(bbb, xf - 1, yf - 1, zf - 1), u);
        var y2 = Lerp(x3, x4, v);

        return Lerp(y1, y2, w);
    }

    private static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);

    private static double Lerp(double a, double b, double t) => a + t * (b - a);

    private static double Grad(int hash, double x, double y, double z)
    {
        return (hash & 1) switch
        {
            0 => x + y,
            _ => -x - y,
        };
    }
}
