using System;
using Layeron.Big3DArray;

namespace Layeron.App.Filters;

/// <summary>
/// The noisy phantom used by the filter runner demo.
/// <para>
/// It is deliberately synthetic and deterministic: a high-contrast sphere with a bright shell, two
/// small lesions and a rod, all buried in Gaussian-like noise plus a few salt-and-pepper outliers.
/// Smoothing and median filters visibly clean the background while the sharp edges and the lesions
/// show whether a filter preserves structure. The same coordinates always produce the same voxel, so a
/// screenshot, a test and a user report all refer to the same data.
/// </para>
/// </summary>
public static class FilterDemoVolume
{
    public const int SizeX = 128;
    public const int SizeY = 128;
    public const int SizeZ = 64;

    /// <summary>Creates the 8-bit phantom.</summary>
    public static Big3DArray<byte> Create()
    {
        var volume = new Big3DArray<byte>(SizeX, SizeY, SizeZ);

        volume.Fill((x, y, z) =>
        {
            // Normalised coordinates in [-1, 1], so the sphere is round in world space rather than
            // stretched by the volume's flat Z dimension.
            var nx = (x - (SizeX - 1) / 2.0) / ((SizeX - 1) / 2.0);
            var ny = (y - (SizeY - 1) / 2.0) / ((SizeY - 1) / 2.0);
            var nz = (z - (SizeZ - 1) / 2.0) / ((SizeZ - 1) / 2.0);

            var value = 28.0 + 18.0 * (1.0 - Math.Min(1.0, Math.Sqrt(nx * nx + ny * ny)));

            var r = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (r < 0.52)
                value = 122;
            else if (r < 0.68)
                value = 196;

            // Two small lesions with different contrast.
            if (Distance(nx, ny, nz, 0.36, 0.28, 0.18) < 0.14)
                value = 242;
            if (Distance(nx, ny, nz, -0.42, -0.3, 0.24) < 0.1)
                value = 214;

            // A rod crossing the lower part of the volume, useful for judging edge preservation.
            if (Math.Sqrt((ny + 0.36) * (ny + 0.36) + (nz - 0.42) * (nz - 0.42)) < 0.075)
                value = 176;

            // Deterministic noise: roughly uniform in +/- 26, plus sparse salt-and-pepper outliers.
            var noise = ((int)(Hash(x, y, z, 0x9E3779B9) % 2001) - 1000) / 1000.0 * 26.0;
            var outlier = Hash(x, y, z, 0x85EBCA6B) & 1023;
            if (outlier == 0)
                noise += 82;
            else if (outlier == 1)
                noise -= 82;

            return (byte)Math.Clamp((int)Math.Round(value + noise), 0, 255);
        });

        return volume;
    }

    private static double Distance(double x, double y, double z, double cx, double cy, double cz)
    {
        var dx = x - cx;
        var dy = y - cy;
        var dz = z - cz;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>
    /// Coordinate hash. Integer arithmetic with unchecked overflow is the point: it gives reproducible
    /// noise per voxel without a shared random generator and without making the result depend on the
    /// order the volume happens to be traversed in.
    /// </summary>
    private static uint Hash(int x, int y, int z, uint seed)
    {
        unchecked
        {
            var h = seed;
            h ^= (uint)x * 0x27D4EB2Du;
            h = (h << 13) | (h >> 19);
            h ^= (uint)y * 0x165667B1u;
            h = (h << 11) | (h >> 21);
            h ^= (uint)z * 0x9E3779B1u;
            h ^= h >> 15;
            h *= 0x85EBCA77u;
            h ^= h >> 13;
            return h;
        }
    }
}
