using System;

namespace Layeron.App.Tools.Geometry;

/// <summary>
/// Signed-distance-field blending of two single-slice regions, used to fill the slices between two
/// user-drawn contours.
/// <para>
/// Each region is converted to a signed distance field with the convention "positive outside,
/// negative inside", then the two fields are linearly mixed and thresholded at zero. Chamfer
/// transforms (weights 3 and 4, divided by 3) approximate the Euclidean distance in two passes, so
/// the cost is linear in the number of pixels.
/// </para>
/// </summary>
public static class ShapeInterpolation
{
    /// <summary>
    /// Computes the signed distance field of a region, indexed <c>[V, U]</c> like the input.
    /// <para>
    /// The value is negative inside the region (distance to the nearest outside pixel) and positive
    /// outside it (distance to the nearest inside pixel); boundary pixels therefore hold
    /// <c>-1</c> and their outside neighbours <c>+1</c>. Distances are chamfer approximations in
    /// pixels, saturated at a finite cap when a region fills the whole plane (or is empty), so an
    /// empty region is uniformly positive and a full one uniformly negative.
    /// </para>
    /// </summary>
    /// <param name="region">The region, indexed <c>[V, U]</c>.</param>
    /// <returns>A <c>[V, U]</c> distance field with the same dimensions as the input.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="region"/> is null.</exception>
    public static double[,] SignedDistance(bool[,] region)
    {
        ArgumentNullException.ThrowIfNull(region);

        var planeV = region.GetLength(0);
        var planeU = region.GetLength(1);
        if (planeV == 0 || planeU == 0)
            return new double[planeV, planeU];

        var toForeground = ChamferDistance(region, sourceIsForeground: true, planeV, planeU);
        var toBackground = ChamferDistance(region, sourceIsForeground: false, planeV, planeU);

        var distance = new double[planeV, planeU];
        for (var v = 0; v < planeV; v++)
        {
            for (var u = 0; u < planeU; u++)
                distance[v, u] = (toForeground[v, u] - toBackground[v, u]) / 3.0;
        }

        return distance;
    }

    /// <summary>
    /// Blends two signed distance fields: a pixel becomes foreground when
    /// <c>(1 - w) * distA + w * distB &lt;= 0</c>. <paramref name="w"/> is clamped to
    /// <c>[0, 1]</c>, so <c>0</c> reproduces the first shape and <c>1</c> the second.
    /// </summary>
    /// <param name="distA">Distance field of the start shape (<c>[V, U]</c>).</param>
    /// <param name="distB">Distance field of the end shape (<c>[V, U]</c>).</param>
    /// <param name="w">Interpolation weight; clamped to <c>[0, 1]</c>.</param>
    /// <returns>A <c>[V, U]</c> region of the blended shape.</returns>
    /// <exception cref="ArgumentNullException">A distance field is null.</exception>
    /// <exception cref="ArgumentException">The fields do not have identical dimensions.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="w"/> is NaN.</exception>
    public static bool[,] Blend(double[,] distA, double[,] distB, double w)
    {
        ArgumentNullException.ThrowIfNull(distA);
        ArgumentNullException.ThrowIfNull(distB);
        if (distA.GetLength(0) != distB.GetLength(0) || distA.GetLength(1) != distB.GetLength(1))
            throw new ArgumentException("Distance fields must have identical dimensions.", nameof(distB));
        if (double.IsNaN(w))
            throw new ArgumentOutOfRangeException(nameof(w), "Interpolation weight must be a number.");

        w = Math.Clamp(w, 0.0, 1.0);
        var weightA = 1.0 - w;
        var planeV = distA.GetLength(0);
        var planeU = distA.GetLength(1);
        var region = new bool[planeV, planeU];
        for (var v = 0; v < planeV; v++)
        {
            for (var u = 0; u < planeU; u++)
                region[v, u] = weightA * distA[v, u] + w * distB[v, u] <= 0.0;
        }

        return region;
    }

    /// <summary>
    /// Convenience wrapper: computes both signed distance fields and blends them.
    /// Empty regions are legal; at the matching end of the interpolation an empty input yields an
    /// empty result because its distance field is entirely positive.
    /// </summary>
    /// <param name="regionA">Start region (<c>[V, U]</c>).</param>
    /// <param name="regionB">End region (<c>[V, U]</c>).</param>
    /// <param name="w">Interpolation weight; clamped to <c>[0, 1]</c>.</param>
    /// <returns>A <c>[V, U]</c> region of the blended shape.</returns>
    /// <exception cref="ArgumentNullException">A region is null.</exception>
    /// <exception cref="ArgumentException">The regions do not have identical dimensions.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="w"/> is NaN.</exception>
    public static bool[,] InterpolateRegion(bool[,] regionA, bool[,] regionB, double w)
    {
        ArgumentNullException.ThrowIfNull(regionA);
        ArgumentNullException.ThrowIfNull(regionB);
        if (regionA.GetLength(0) != regionB.GetLength(0) || regionA.GetLength(1) != regionB.GetLength(1))
            throw new ArgumentException("Regions must have identical dimensions.", nameof(regionB));

        return Blend(SignedDistance(regionA), SignedDistance(regionB), w);
    }

    /// <summary>
    /// Two-pass chamfer distance from every pixel to the nearest pixel whose value equals
    /// <paramref name="sourceIsForeground"/>. Distances are stored in thirds of a pixel (3 per
    /// orthogonal step, 4 per diagonal), keeping the transform integer and allocation free beyond
    /// the result buffer. Pixels with no source saturate at a finite cap.
    /// </summary>
    private static int[,] ChamferDistance(bool[,] region, bool sourceIsForeground, int planeV, int planeU)
    {
        const int Straight = 3;
        const int Diagonal = 4;
        var cap = (planeV + planeU) * Diagonal + Diagonal;

        var distance = new int[planeV, planeU];
        for (var v = 0; v < planeV; v++)
        {
            for (var u = 0; u < planeU; u++)
                distance[v, u] = region[v, u] == sourceIsForeground ? 0 : cap;
        }

        // Forward pass: neighbours already visited are above and to the left.
        for (var v = 0; v < planeV; v++)
        {
            for (var u = 0; u < planeU; u++)
            {
                var best = distance[v, u];
                if (u > 0)
                    best = Math.Min(best, distance[v, u - 1] + Straight);
                if (v > 0)
                {
                    best = Math.Min(best, distance[v - 1, u] + Straight);
                    if (u > 0)
                        best = Math.Min(best, distance[v - 1, u - 1] + Diagonal);
                    if (u + 1 < planeU)
                        best = Math.Min(best, distance[v - 1, u + 1] + Diagonal);
                }

                distance[v, u] = best;
            }
        }

        // Backward pass: neighbours below and to the right.
        for (var v = planeV - 1; v >= 0; v--)
        {
            for (var u = planeU - 1; u >= 0; u--)
            {
                var best = distance[v, u];
                if (u + 1 < planeU)
                    best = Math.Min(best, distance[v, u + 1] + Straight);
                if (v + 1 < planeV)
                {
                    best = Math.Min(best, distance[v + 1, u] + Straight);
                    if (u + 1 < planeU)
                        best = Math.Min(best, distance[v + 1, u + 1] + Diagonal);
                    if (u > 0)
                        best = Math.Min(best, distance[v + 1, u - 1] + Diagonal);
                }

                distance[v, u] = best;
            }
        }

        return distance;
    }
}
