using System.IO;
using Layeron.App.Service;
using Layeron.Big3DArray;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace Layeron.App.Test;

/// <summary>
/// The image import must not force every series through 8 bits: a 16-bit PNG/TIFF series has to
/// keep its values, otherwise thresholds and window/level operate on quantized data.
/// </summary>
[TestClass]
public sealed class ImageVolumeLoaderTests
{
    private const int Width = 5;
    private const int Height = 4;

    private static string WriteGray8(TempDirectory temp, string name, byte value)
    {
        var path = temp.Path(name);
        using var image = new Image<L8>(Width, Height);
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
            image[x, y] = new L8(value);
        image.Save(path, new PngEncoder());
        return path;
    }

    private static string WriteGray16(TempDirectory temp, string name, ushort value)
    {
        var path = temp.Path(name);
        using var image = new Image<L16>(Width, Height);
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
            image[x, y] = new L16(value);
        image.Save(path, new PngEncoder { BitDepth = PngBitDepth.Bit16, ColorType = PngColorType.Grayscale });
        return path;
    }

    [TestMethod]
    public void EightBitSeries_ProducesByteVolume()
    {
        using var temp = new TempDirectory();
        var paths = new[]
        {
            WriteGray8(temp, "s0.png", 10),
            WriteGray8(temp, "s1.png", 200),
        };

        var info = ImageVolumeLoader.Probe(paths);
        Assert.AreEqual(8, info.BitsPerComponent);
        Assert.IsFalse(info.Is16Bit);

        var volume = ImageVolumeLoader.Load(paths, info);
        Assert.AreEqual(PixelElementType.Byte, volume.ElementType);

        var typed = (ReadonlyBig3DArray<byte>)volume;
        Assert.AreEqual(Width, typed.X);
        Assert.AreEqual(Height, typed.Y);
        Assert.AreEqual(2, typed.Z);
        Assert.AreEqual(10, typed[0, 0, 0]);
        Assert.AreEqual(200, typed[Width - 1, Height - 1, 1]);
    }

    [TestMethod]
    public void SixteenBitSeries_KeepsFullPrecision()
    {
        // These values collapse when quantized to 8 bits: 255/256/257 all become 1.
        var values = new ushort[] { 0, 255, 256, 257, 1000, 32768, 65535 };

        using var temp = new TempDirectory();
        var paths = new string[values.Length];
        for (var z = 0; z < values.Length; z++)
            paths[z] = WriteGray16(temp, $"s{z}.png", values[z]);

        var info = ImageVolumeLoader.Probe(paths);
        Assert.AreEqual(16, info.BitsPerComponent);
        Assert.IsTrue(info.Is16Bit);

        var volume = ImageVolumeLoader.Load(paths, info);
        Assert.AreEqual(PixelElementType.UInt16, volume.ElementType);

        var typed = (ReadonlyBig3DArray<ushort>)volume;
        for (var z = 0; z < values.Length; z++)
        {
            Assert.AreEqual(values[z], typed[0, 0, z], $"z={z}");
            Assert.AreEqual(values[z], typed[Width - 1, Height - 1, z], $"z={z}");
        }
    }

    [TestMethod]
    public void EightBitColourSeries_IsNotMistakenForHighBitDepth()
    {
        using var temp = new TempDirectory();
        var paths = new string[2];
        for (var z = 0; z < paths.Length; z++)
        {
            var path = temp.Path($"c{z}.png");
            using var image = new Image<Rgba32>(Width, Height, new Rgba32((byte)(10 + z), 20, 30));
            image.Save(path, new PngEncoder());
            paths[z] = path;
        }

        var info = ImageVolumeLoader.Probe(paths);
        Assert.AreEqual(8, info.BitsPerComponent);
        Assert.AreEqual(PixelElementType.Byte, ImageVolumeLoader.Load(paths, info).ElementType);
    }

    [TestMethod]
    public void MixedBitDepths_AreRejected()
    {
        using var temp = new TempDirectory();
        var paths = new[]
        {
            WriteGray16(temp, "s0.png", 300),
            WriteGray8(temp, "s1.png", 7),
        };

        var error = Assert.Throws<InvalidDataException>(() => ImageVolumeLoader.Probe(paths));
        StringAssert.Contains(error.Message, "bit depths");
    }

    [TestMethod]
    public void DifferentSizes_AreRejected()
    {
        using var temp = new TempDirectory();
        var smaller = temp.Path("small.png");
        using (var image = new Image<L8>(Width - 1, Height))
            image.Save(smaller, new PngEncoder());

        var paths = new[] { WriteGray8(temp, "s0.png", 1), smaller };

        var error = Assert.Throws<InvalidDataException>(() => ImageVolumeLoader.Probe(paths));
        StringAssert.Contains(error.Message, "cannot form one volume");
    }
}
