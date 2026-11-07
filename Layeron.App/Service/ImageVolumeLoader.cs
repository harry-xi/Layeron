using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Layeron.Big3DArray;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Layeron.App.Service;

/// <summary>
/// Builds a volume from a sequence of 2D image files, one slice per file.
/// <para>
/// The bit depth is detected from the files instead of being forced: 16-bit PNG/TIFF series keep
/// their full range (loaded into a <c>Big3DArray&lt;ushort&gt;</c>), while 8-bit series keep the
/// original byte values. Loading everything through an 8-bit pixel format - what the panel used to
/// do - silently quantizes 16-bit data (256 and 257 both become 1), which destroys window/level and
/// threshold results.
/// </para>
/// </summary>
public static class ImageVolumeLoader
{
    /// <summary>Shape and sample depth shared by every image of one import.</summary>
    public readonly record struct ImageSetInfo(int Width, int Height, int BitsPerComponent)
    {
        public bool Is16Bit => BitsPerComponent > 8;

        public PixelElementType ElementType => Is16Bit ? PixelElementType.UInt16 : PixelElementType.Byte;
    }

    /// <summary>
    /// Reads the headers of every image and verifies they can form one volume: same size, same
    /// sample depth. Throws <see cref="InvalidDataException"/> with the offending file name otherwise.
    /// </summary>
    public static ImageSetInfo Probe(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            throw new ArgumentException("At least one image is required.", nameof(paths));

        var first = Identify(paths[0]);
        var bits = BitsPerComponent(first.PixelType);
        var is16Bit = bits > 8;

        for (var i = 1; i < paths.Count; i++)
        {
            var info = Identify(paths[i]);

            if (info.Width != first.Width || info.Height != first.Height)
            {
                throw new InvalidDataException(
                    $"'{Path.GetFileName(paths[i])}' is {info.Width}x{info.Height} but " +
                    $"'{Path.GetFileName(paths[0])}' is {first.Width}x{first.Height}; " +
                    "the images cannot form one volume.");
            }

            var fileBits = BitsPerComponent(info.PixelType);
            if ((fileBits > 8) != is16Bit)
            {
                throw new InvalidDataException(
                    $"'{Path.GetFileName(paths[i])}' stores {fileBits}-bit samples while " +
                    $"'{Path.GetFileName(paths[0])}' stores {bits}-bit samples; mixing bit depths in " +
                    "one volume would change the value scale, so convert the series to a single depth first.");
            }
        }

        return new ImageSetInfo(first.Width, first.Height, bits);
    }

    /// <summary>Builds the volume described by <paramref name="info"/>.</summary>
    public static ReadonlyBig3DArray Load(IReadOnlyList<string> paths, ImageSetInfo info)
    {
        return info.Is16Bit
            ? Big3DArray<ushort>.FromImages(paths, Read16, info.Width, info.Height, paths.Count)
            : Big3DArray<byte>.FromImages(paths, Read8, info.Width, info.Height, paths.Count);
    }

    public static ReadonlyBig3DArray Load(IReadOnlyList<string> paths) => Load(paths, Probe(paths));

    /// <summary>Bits per single sample (per channel), e.g. 16 for 16-bit grayscale PNG/TIFF.</summary>
    private static int BitsPerComponent(PixelTypeInfo pixelType)
    {
        var components = pixelType.ComponentInfo?.ComponentCount ?? EstimateComponents(pixelType.ColorType);
        return components > 0 ? pixelType.BitsPerPixel / components : pixelType.BitsPerPixel;
    }

    /// <summary>
    /// Channel count for formats that do not report component information, so a 24-bit JPEG is not
    /// mistaken for a high bit-depth image.
    /// </summary>
    private static int EstimateComponents(PixelColorType colorType)
    {
        if (colorType.HasFlag(PixelColorType.Luminance) || colorType.HasFlag(PixelColorType.Indexed))
            return colorType.HasFlag(PixelColorType.Alpha) ? 2 : 1;
        if (colorType.HasFlag(PixelColorType.CMYK) || colorType.HasFlag(PixelColorType.YCCK))
            return 4;
        if (colorType.HasFlag(PixelColorType.YCbCr))
            return 3;

        var count = 0;
        if (colorType.HasFlag(PixelColorType.Red)) count++;
        if (colorType.HasFlag(PixelColorType.Green)) count++;
        if (colorType.HasFlag(PixelColorType.Blue)) count++;
        if (count == 0) count = 1;
        if (colorType.HasFlag(PixelColorType.Alpha)) count++;
        return count;
    }

    private static ImageInfo Identify(string path)
    {
        try
        {
            return Image.Identify(path);
        }
        catch (UnknownImageFormatException ex)
        {
            throw new InvalidDataException($"'{path}' is not a supported image file.", ex);
        }
    }

    private static byte[,] Read8(string path)
    {
        using var image = Image.Load<L8>(path);
        var width = image.Width;
        var height = image.Height;
        var data = new byte[height, width];

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < height; y++)
            {
                var row = MemoryMarshal.Cast<L8, byte>(accessor.GetRowSpan(y));
                row.CopyTo(MemoryMarshal.CreateSpan(ref data[y, 0], width));
            }
        });

        return data;
    }

    private static ushort[,] Read16(string path)
    {
        using var image = Image.Load<L16>(path);
        var width = image.Width;
        var height = image.Height;
        var data = new ushort[height, width];

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < height; y++)
            {
                var row = MemoryMarshal.Cast<L16, ushort>(accessor.GetRowSpan(y));
                row.CopyTo(MemoryMarshal.CreateSpan(ref data[y, 0], width));
            }
        });

        return data;
    }
}
