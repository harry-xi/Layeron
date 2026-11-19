using System;
using System.Collections.Generic;

namespace Layeron.App.Tools.Geometry;

/// <summary>
/// Pure rasterization of tool shapes into slice regions.
/// <para>
/// Every method returns a <c>bool[,]</c> indexed <c>[V, U]</c> (rows = V, columns = U) and sized to the
/// whole plane, matching the slice buffers described by <see cref="SliceGeometry"/>. Shapes are
/// clipped to the plane; coordinates are continuous pixel positions where an integer value denotes a
/// pixel centre, so a region cell <c>[v, u]</c> covers the unit square around <c>(u, v)</c>.
/// </para>
/// </summary>
public static class Raster
{
    /// <summary>Tolerance used when a boundary lands exactly on a pixel centre.</summary>
    private const double Epsilon = 1e-9;

    /// <summary>
    /// Stamps a filled disc of the given radius centred at <c>(u, v)</c>, clipped to the plane.
    /// A pixel is set when its centre lies inside the disc (inclusive of the boundary).
    /// <para>
    /// A radius of zero or less degenerates to the single pixel at
    /// <c>(floor(u), floor(v))</c>; a centre outside the plane therefore yields an empty region
    /// rather than an exception.
    /// </para>
    /// </summary>
    /// <param name="u">Disc centre, horizontal pixel coordinate.</param>
    /// <param name="v">Disc centre, vertical pixel coordinate.</param>
    /// <param name="radius">Disc radius in pixels.</param>
    /// <param name="planeU">Plane width (number of columns).</param>
    /// <param name="planeV">Plane height (number of rows).</param>
    /// <returns>A <c>[V, U]</c> region containing the clipped disc.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A plane dimension is negative.</exception>
    public static bool[,] StampDisc(double u, double v, double radius, int planeU, int planeV)
    {
        ValidatePlane(planeU, planeV);

        var region = new bool[planeV, planeU];
        if (!double.IsFinite(u) || !double.IsFinite(v) || !double.IsFinite(radius))
            return region;

        if (radius <= 0.0)
        {
            if (u >= 0.0 && u < planeU && v >= 0.0 && v < planeV)
                region[(int)Math.Floor(v), (int)Math.Floor(u)] = true;
            return region;
        }

        var r2 = radius * radius;
        var left = Math.Max(0.0, Math.Ceiling(u - radius - Epsilon));
        var right = Math.Min(planeU - 1.0, Math.Floor(u + radius + Epsilon));
        var top = Math.Max(0.0, Math.Ceiling(v - radius - Epsilon));
        var bottom = Math.Min(planeV - 1.0, Math.Floor(v + radius + Epsilon));
        if (left > right || top > bottom)
            return region;

        var x0 = (int)left;
        var x1 = (int)right;
        var y0 = (int)top;
        var y1 = (int)bottom;
        for (var y = y0; y <= y1; y++)
        {
            var dy = y - v;
            var dy2 = dy * dy;
            for (var x = x0; x <= x1; x++)
            {
                var dx = x - u;
                if (dx * dx + dy2 <= r2 + Epsilon)
                    region[y, x] = true;
            }
        }

        return region;
    }

    /// <summary>
    /// Samples a line segment densely enough to bridge pointer-move gaps: consecutive samples are at
    /// most one pixel apart, so stamping a disc of any radius of one or more at every sample leaves
    /// no holes. Both endpoints are always present (a zero-length segment yields one sample).
    /// </summary>
    /// <param name="u0">Start U coordinate.</param>
    /// <param name="v0">Start V coordinate.</param>
    /// <param name="u1">End U coordinate.</param>
    /// <param name="v1">End V coordinate.</param>
    /// <returns>An ordered, dense polyline from start to end.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate is not finite, or the segment is too long to sample.</exception>
    public static IReadOnlyList<(double U, double V)> InterpolateLine(double u0, double v0, double u1, double v1)
    {
        if (!double.IsFinite(u0) || !double.IsFinite(v0) || !double.IsFinite(u1) || !double.IsFinite(v1))
            throw new ArgumentOutOfRangeException(nameof(u0), "Line endpoints must be finite.");

        var du = u1 - u0;
        var dv = v1 - v0;
        var length = Math.Sqrt(du * du + dv * dv);
        if (!double.IsFinite(length) || length >= int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(u1), "Line segment is too long to sample.");

        var steps = (int)Math.Ceiling(length);
        if (steps <= 0)
            return new[] { (u0, v0) };

        var points = new (double U, double V)[steps + 1];
        for (var i = 0; i <= steps; i++)
        {
            if (i == 0)
            {
                points[i] = (u0, v0);
                continue;
            }

            if (i == steps)
            {
                points[i] = (u1, v1);
                continue;
            }

            var t = (double)i / steps;
            points[i] = (u0 + du * t, v0 + dv * t);
        }

        return points;
    }

    /// <summary>
    /// Fills a closed polygon using the scanline even-odd rule, then adds the rasterized boundary so
    /// a thin or zero-area polygon cannot vanish. Concave and self-intersecting polygons are legal;
    /// the even-odd rule leaves self-overlapping lobes as alternating filled/empty spans.
    /// <para>Fewer than three vertices produce an empty region. Output is clipped to the plane.</para>
    /// </summary>
    /// <param name="vertices">Polygon vertices in order; the closing edge is implied.</param>
    /// <param name="planeU">Plane width (number of columns).</param>
    /// <param name="planeV">Plane height (number of rows).</param>
    /// <returns>A <c>[V, U]</c> region containing the filled polygon.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="vertices"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A plane dimension is negative or a vertex is not finite.</exception>
    public static bool[,] FillPolygon(IReadOnlyList<(double U, double V)> vertices, int planeU, int planeV)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ValidatePlane(planeU, planeV);

        var region = new bool[planeV, planeU];
        var count = vertices.Count;
        if (count < 3 || planeU == 0 || planeV == 0)
            return region;

        for (var i = 0; i < count; i++)
        {
            if (!double.IsFinite(vertices[i].U) || !double.IsFinite(vertices[i].V))
                throw new ArgumentOutOfRangeException(nameof(vertices), "Polygon vertices must be finite.");
        }

        // Boundary first: a degenerate polygon (collinear vertices) has no area but is still a stroke.
        for (var i = 0; i < count; i++)
        {
            var a = vertices[i];
            var b = vertices[(i + 1) % count];
            DrawLine(
                region,
                ToPixel(a.U, planeU),
                ToPixel(a.V, planeV),
                ToPixel(b.U, planeU),
                ToPixel(b.V, planeV));
        }

        // One reusable buffer: a scanline can cross each edge at most once.
        var crossings = new double[count];
        for (var y = 0; y < planeV; y++)
        {
            var n = 0;
            var scan = (double)y;
            for (var i = 0; i < count; i++)
            {
                var a = vertices[i];
                var b = vertices[(i + 1) % count];

                // Half-open rule: an edge contributes when the scanline is at or above its lower V
                // and strictly below its upper V. A shared vertex is therefore counted exactly once,
                // which is what keeps the parity correct at concave corners.
                if ((a.V <= scan && b.V > scan) || (b.V <= scan && a.V > scan))
                {
                    var t = (scan - a.V) / (b.V - a.V);
                    crossings[n++] = a.U + t * (b.U - a.U);
                }
            }

            if (n < 2)
                continue;

            InsertionSort(crossings, n);
            for (var k = 0; k + 1 < n; k += 2)
            {
                var left = crossings[k];
                var right = crossings[k + 1];
                if (right < 0.0 || left > planeU - 1.0)
                    continue;

                var start = (int)Math.Max(0.0, Math.Ceiling(left - Epsilon));
                var end = (int)Math.Min(planeU - 1.0, Math.Floor(right + Epsilon));
                for (var x = start; x <= end; x++)
                    region[y, x] = true;
            }
        }

        return region;
    }

    /// <summary>
    /// Rasterizes an axis-aligned, inclusive rectangle from two opposite corners given in any order.
    /// Corners are rounded to the nearest pixel; the region includes both boundary pixels.
    /// </summary>
    /// <param name="u0">First corner U coordinate.</param>
    /// <param name="v0">First corner V coordinate.</param>
    /// <param name="u1">Opposite corner U coordinate.</param>
    /// <param name="v1">Opposite corner V coordinate.</param>
    /// <param name="planeU">Plane width (number of columns).</param>
    /// <param name="planeV">Plane height (number of rows).</param>
    /// <returns>A <c>[V, U]</c> region containing the clipped rectangle.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A plane dimension is negative.</exception>
    public static bool[,] Rectangle(double u0, double v0, double u1, double v1, int planeU, int planeV)
    {
        ValidatePlane(planeU, planeV);

        var region = new bool[planeV, planeU];
        if (planeU == 0 || planeV == 0)
            return region;
        if (!double.IsFinite(u0) || !double.IsFinite(v0) || !double.IsFinite(u1) || !double.IsFinite(v1))
            return region;

        var left = Math.Min(RoundPixel(u0), RoundPixel(u1));
        var right = Math.Max(RoundPixel(u0), RoundPixel(u1));
        var top = Math.Min(RoundPixel(v0), RoundPixel(v1));
        var bottom = Math.Max(RoundPixel(v0), RoundPixel(v1));

        if (right < 0 || left > planeU - 1 || bottom < 0 || top > planeV - 1)
            return region;

        left = Math.Clamp(left, 0, planeU - 1);
        right = Math.Clamp(right, 0, planeU - 1);
        top = Math.Clamp(top, 0, planeV - 1);
        bottom = Math.Clamp(bottom, 0, planeV - 1);

        for (var y = top; y <= bottom; y++)
            for (var x = left; x <= right; x++)
                region[y, x] = true;

        return region;
    }

    /// <summary>
    /// Fills the ellipse inscribed in the axis-aligned rectangle defined by two corners given in any
    /// order. A degenerate axis collapses to a line (or a single pixel when both axes are zero),
    /// matching the rectangle between the same corners.
    /// </summary>
    /// <param name="u0">First corner U coordinate.</param>
    /// <param name="v0">First corner V coordinate.</param>
    /// <param name="u1">Opposite corner U coordinate.</param>
    /// <param name="v1">Opposite corner V coordinate.</param>
    /// <param name="planeU">Plane width (number of columns).</param>
    /// <param name="planeV">Plane height (number of rows).</param>
    /// <returns>A <c>[V, U]</c> region containing the clipped filled ellipse.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A plane dimension is negative.</exception>
    public static bool[,] Ellipse(double u0, double v0, double u1, double v1, int planeU, int planeV)
    {
        ValidatePlane(planeU, planeV);

        var region = new bool[planeV, planeU];
        if (planeU == 0 || planeV == 0)
            return region;
        if (!double.IsFinite(u0) || !double.IsFinite(v0) || !double.IsFinite(u1) || !double.IsFinite(v1))
            return region;

        var leftPixel = RoundPixel(u0);
        var rightPixel = RoundPixel(u1);
        var topPixel = RoundPixel(v0);
        var bottomPixel = RoundPixel(v1);
        if (leftPixel == rightPixel || topPixel == bottomPixel)
            return Rectangle(u0, v0, u1, v1, planeU, planeV);

        var left = Math.Min(u0, u1);
        var right = Math.Max(u0, u1);
        var top = Math.Min(v0, v1);
        var bottom = Math.Max(v0, v1);

        var centerU = (left + right) * 0.5;
        var centerV = (top + bottom) * 0.5;
        var radiusU = (right - left) * 0.5;
        var radiusV = (bottom - top) * 0.5;
        if (radiusU <= 0.0 || radiusV <= 0.0)
            return Rectangle(u0, v0, u1, v1, planeU, planeV);

        var x0 = Math.Clamp(Math.Min(leftPixel, rightPixel), 0, planeU - 1);
        var x1 = Math.Clamp(Math.Max(leftPixel, rightPixel), 0, planeU - 1);
        var y0 = Math.Clamp(Math.Min(topPixel, bottomPixel), 0, planeV - 1);
        var y1 = Math.Clamp(Math.Max(topPixel, bottomPixel), 0, planeV - 1);

        if (Math.Max(leftPixel, rightPixel) < 0 || Math.Min(leftPixel, rightPixel) > planeU - 1 ||
            Math.Max(topPixel, bottomPixel) < 0 || Math.Min(topPixel, bottomPixel) > planeV - 1)
            return region;

        for (var y = y0; y <= y1; y++)
        {
            var ny = (y - centerV) / radiusV;
            var ny2 = ny * ny;
            for (var x = x0; x <= x1; x++)
            {
                var nx = (x - centerU) / radiusU;
                if (nx * nx + ny2 <= 1.0 + Epsilon)
                    region[y, x] = true;
            }
        }

        return region;
    }

    private static void ValidatePlane(int planeU, int planeV)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(planeU);
        ArgumentOutOfRangeException.ThrowIfNegative(planeV);
    }

    /// <summary>Rounds a continuous coordinate to a pixel index, saturating instead of overflowing.</summary>
    private static int RoundPixel(double value)
    {
        if (value <= int.MinValue)
            return int.MinValue;
        if (value >= int.MaxValue)
            return int.MaxValue;
        return (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Rounds to a pixel index but saturates to an off-plane sentinel when the value is outside the
    /// plane, so a clipped edge is simply skipped by the rasterizer.
    /// </summary>
    private static int ToPixel(double value, int limit)
    {
        if (value <= -1.0)
            return -1;
        if (value >= limit)
            return limit;
        return (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    private static void DrawLine(bool[,] region, int x0, int y0, int x1, int y1)
    {
        var dx = Math.Abs(x1 - x0);
        var sx = x0 < x1 ? 1 : -1;
        var dy = -Math.Abs(y1 - y0);
        var sy = y0 < y1 ? 1 : -1;
        var error = dx + dy;
        var height = region.GetLength(0);
        var width = region.GetLength(1);

        while (true)
        {
            if ((uint)x0 < (uint)width && (uint)y0 < (uint)height)
                region[y0, x0] = true;

            if (x0 == x1 && y0 == y1)
                break;

            var doubled = 2 * error;
            if (doubled >= dy)
            {
                error += dy;
                x0 += sx;
            }

            if (doubled <= dx)
            {
                error += dx;
                y0 += sy;
            }
        }
    }

    private static void InsertionSort(double[] values, int count)
    {
        for (var i = 1; i < count; i++)
        {
            var value = values[i];
            var j = i - 1;
            while (j >= 0 && values[j] > value)
            {
                values[j + 1] = values[j];
                j--;
            }

            values[j + 1] = value;
        }
    }
}
