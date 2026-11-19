using System.Numerics;
using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;

namespace Layeron.Plugins.Filters;

/// <summary>
/// The built-in neighbourhood filters, expressed once against <see cref="IVolumeFilter"/>.
/// <para>
/// All four are 3D 3x3x3 kernels with a fixed parameter set, deliberately: the contract has no
/// parameter model, so each algorithm is a separate filter id. The host's filter runner lists them,
/// previews one slice and applies the whole volume through the same <c>Run</c> method - which is what
/// keeps a preview and an apply voxel-for-voxel identical.
/// </para>
/// <para>
/// Every loop writes only inside <see cref="FilterRegion"/>, reads neighbours through
/// <see cref="VolumeFilterSampling.GetClamped{T}"/> (the contract's single edge policy), checks the
/// cancellation token once per block and reports progress once per block.
/// </para>
/// </summary>
internal sealed class NeighborhoodFilter : IVolumeFilter
{
    private enum Kernel
    {
        Mean,
        Gaussian,
        Median,
        Sharpen,
        Gradient
    }

    private readonly Kernel _kernel;

    private NeighborhoodFilter(string id, string displayName, string description, Kernel kernel)
    {
        Id = id;
        DisplayName = displayName;
        Description = description;
        _kernel = kernel;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string? Description { get; }

    public static NeighborhoodFilter Mean { get; } = new(
        "layeron.filters.mean3",
        "3×3×3 均值",
        "邻域均值平滑，适合快速抑制随机噪声。",
        Kernel.Mean);

    public static NeighborhoodFilter Gaussian { get; } = new(
        "layeron.filters.gaussian3",
        "3×3×3 高斯",
        "可分离的 1-2-1 高斯平滑，比均值更好地保留结构。",
        Kernel.Gaussian);

    public static NeighborhoodFilter Median { get; } = new(
        "layeron.filters.median3",
        "3×3×3 中值",
        "邻域中值，对椒盐噪声特别有效，同时保留边缘。",
        Kernel.Median);

    public static NeighborhoodFilter Sharpen { get; } = new(
        "layeron.filters.sharpen3",
        "3×3×3 锐化",
        "反锐化掩模：在平滑结果上增强中心体素，突出边界。",
        Kernel.Sharpen);

    public static NeighborhoodFilter Gradient { get; } = new(
        "layeron.filters.gradient3",
        "3×3×3 梯度幅值",
        "六邻域中心差分梯度，用于显示边缘强度。",
        Kernel.Gradient);

    public static IReadOnlyList<NeighborhoodFilter> All { get; } =
    [
        Mean,
        Gaussian,
        Median,
        Sharpen,
        Gradient
    ];

    public void Run<T>(ReadonlyBig3DArray<T> src, Big3DArray<T> dst, FilterRegion region, FilterContext ctx)
        where T : unmanaged, INumber<T>
    {
        if (region.IsEmpty)
            return;

        // Region coordinates are always inside the source volume, but intersecting with each block
        // keeps the loops honest for a one-slice preview as well as for a whole-volume apply.
        for (var block = 0; block < src.BlockCount; block++)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();

            var origin = src.GetBlockOrigin(block);
            var x0 = Math.Max(origin.X, region.X0);
            var y0 = Math.Max(origin.Y, region.Y0);
            var z0 = Math.Max(origin.Z, region.Z0);
            var x1 = Math.Min(origin.X + ReadonlyBig3DArray.BlockEdge, region.X0 + region.X);
            var y1 = Math.Min(origin.Y + ReadonlyBig3DArray.BlockEdge, region.Y0 + region.Y);
            var z1 = Math.Min(origin.Z + ReadonlyBig3DArray.BlockEdge, region.Z0 + region.Z);

            for (var z = z0; z < z1; z++)
            {
                for (var y = y0; y < y1; y++)
                {
                    for (var x = x0; x < x1; x++)
                        dst[x, y, z] = Compute(src, x, y, z);
                }
            }

            ctx.Progress?.Invoke((block + 1) / (double)src.BlockCount);
        }
    }

    private T Compute<T>(ReadonlyBig3DArray<T> src, int x, int y, int z)
        where T : unmanaged, INumber<T>
    {
        return _kernel switch
        {
            Kernel.Mean => MeanAt(src, x, y, z),
            Kernel.Gaussian => GaussianAt(src, x, y, z),
            Kernel.Median => MedianAt(src, x, y, z),
            Kernel.Sharpen => SharpenAt(src, x, y, z),
            _ => GradientAt(src, x, y, z)
        };
    }

    private static T MeanAt<T>(ReadonlyBig3DArray<T> src, int x, int y, int z)
        where T : unmanaged, INumber<T>
    {
        var sum = 0.0;

        for (var dz = -1; dz <= 1; dz++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
            sum += double.CreateChecked(VolumeFilterSampling.GetClamped(src, x + dx, y + dy, z + dz));

        return T.CreateSaturating(sum / 27.0);
    }

    private static T GaussianAt<T>(ReadonlyBig3DArray<T> src, int x, int y, int z)
        where T : unmanaged, INumber<T>
    {
        var sum = 0.0;
        var weightSum = 0.0;

        for (var dz = -1; dz <= 1; dz++)
        {
            var wz = dz == 0 ? 2 : 1;
            for (var dy = -1; dy <= 1; dy++)
            {
                var wy = dy == 0 ? 2 : 1;
                for (var dx = -1; dx <= 1; dx++)
                {
                    var wx = dx == 0 ? 2 : 1;
                    var weight = wx * wy * wz;
                    sum += double.CreateChecked(VolumeFilterSampling.GetClamped(src, x + dx, y + dy, z + dz)) * weight;
                    weightSum += weight;
                }
            }
        }

        return T.CreateSaturating(sum / weightSum);
    }

    private static T MedianAt<T>(ReadonlyBig3DArray<T> src, int x, int y, int z)
        where T : unmanaged, INumber<T>
    {
        Span<T> values = stackalloc T[27];
        var index = 0;

        for (var dz = -1; dz <= 1; dz++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
            values[index++] = VolumeFilterSampling.GetClamped(src, x + dx, y + dy, z + dz);

        values.Sort();
        return values[13];
    }

    private static T SharpenAt<T>(ReadonlyBig3DArray<T> src, int x, int y, int z)
        where T : unmanaged, INumber<T>
    {
        var sum = 0.0;

        for (var dz = -1; dz <= 1; dz++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
            sum += double.CreateChecked(VolumeFilterSampling.GetClamped(src, x + dx, y + dy, z + dz));

        var center = double.CreateChecked(src[x, y, z]);
        var mean = sum / 27.0;

        // Unsharp masking, amount 1.2. Saturating conversion keeps byte and ushort in range without a
        // second element-type switch.
        return T.CreateSaturating(center + 1.2 * (center - mean));
    }

    private static T GradientAt<T>(ReadonlyBig3DArray<T> src, int x, int y, int z)
        where T : unmanaged, INumber<T>
    {
        var dx = double.CreateChecked(VolumeFilterSampling.GetClamped(src, x + 1, y, z)) -
                 double.CreateChecked(VolumeFilterSampling.GetClamped(src, x - 1, y, z));
        var dy = double.CreateChecked(VolumeFilterSampling.GetClamped(src, x, y + 1, z)) -
                 double.CreateChecked(VolumeFilterSampling.GetClamped(src, x, y - 1, z));
        var dz = double.CreateChecked(VolumeFilterSampling.GetClamped(src, x, y, z + 1)) -
                 double.CreateChecked(VolumeFilterSampling.GetClamped(src, x, y, z - 1));

        return T.CreateSaturating(Math.Sqrt(dx * dx + dy * dy + dz * dz));
    }
}
