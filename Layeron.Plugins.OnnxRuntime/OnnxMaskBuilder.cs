using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;
using Layeron.Plugins.Onnx.Api;

namespace Layeron.Plugins.Onnx.Runtime;

/// <summary>
/// Turns model output into the mask layer the user asked for.
/// <para>
/// The threshold is a fraction of the output range rather than an absolute value, and it is resolved from the
/// range's midpoint, which is where a band-pass model puts flat content. That is what makes "0.6" mean "this much
/// above flat" for every model instead of meaning something different for each one - the demo model, a
/// segmentation model and a denoiser all answer the same way.
/// </para>
/// </summary>
internal static class OnnxMaskBuilder
{
    /// <summary>
    /// Adds a mask layer holding the plane voxels whose output exceeded the binding's threshold, and returns the
    /// layer's name so the caller can report what appeared.
    /// </summary>
    public static string AddPlaneMask(
        IMaskService masks,
        string modelId,
        OnnxSliceResult result,
        OnnxBinding binding,
        ReadonlyBig3DArray volume)
    {
        ArgumentNullException.ThrowIfNull(masks);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(volume);

        var cut = (float)binding.ResolveThreshold(result.Min, result.Max);
        var (width, height) = OnnxVolumeBinder.PlaneSize(volume, result.Plane);
        var bitmap = new Big3DBitMap(volume.X, volume.Y, volume.Z);

        // A tiled read can produce a plane larger than the volume in the axis it was clamped on; keeping the loop
        // inside both buffers means that border is dropped rather than misplace into a neighbouring slice.
        var rows = Math.Min(result.Values.GetLength(0), height);
        var columns = Math.Min(result.Values.GetLength(1), width);
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                if (result.Values[row, column] < cut)
                    continue;

                var (x, y, z) = ToVolume(result.Plane, result.Index, column, row);
                bitmap[x, y, z] = true;
            }
        }

        var name = $"{modelId} 结构（切片 {result.Index}）";
        masks.Add(name, bitmap, MaskColor.Yellow, 0.4);
        masks.NotifyContentChanged();
        return name;
    }

    /// <summary>Maps a plane's own <c>(column, row)</c> onto the volume's <c>(x, y, z)</c>.</summary>
    public static (int X, int Y, int Z) ToVolume(OnnxSlicePlane plane, int index, int column, int row) => plane switch
    {
        OnnxSlicePlane.XY => (column, row, index),
        OnnxSlicePlane.XZ => (column, index, row),
        OnnxSlicePlane.YZ => (index, column, row),
        _ => (column, row, index)
    };

    /// <summary>
    /// Builds the overlay mask a slice preview publishes: the same threshold the mask path would use, so what the
    /// user sees highlighted is exactly what an apply would create.
    /// </summary>
    public static bool[,] BuildOverlay(OnnxSliceResult result, OnnxBinding binding)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(binding);

        var cut = (float)binding.ResolveThreshold(result.Min, result.Max);
        var height = result.Values.GetLength(0);
        var width = result.Values.GetLength(1);
        var mask = new bool[height, width];
        for (var row = 0; row < height; row++)
            for (var column = 0; column < width; column++)
                mask[row, column] = result.Values[row, column] >= cut;

        return mask;
    }
}
