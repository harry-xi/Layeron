using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;
using Layeron.Plugin.Host;
namespace Layeron.Plugin.Test;
/// <summary>
/// Custom data sources: AddLoader lands on the host-owned loader point like AddFilter does, and the
/// slices a loader hands over become the volume's voxels in order.
/// </summary>
[TestClass]
public sealed class LoaderRegistrationTests
{
    [TestMethod]
    public void AddLoader_LandsOnHostOwnedPoint()
    {
        var adapter = new TestHostAdapter(TestVolumes.Small());
        using var manager = new PluginManager(adapter);
        Assert.IsNotNull(manager.LoadInProcess(new SourceLoaderPlugin(), "test.dicom"));
        var point = manager.PointRegistry.GetPoint<VolumeLoader>(LoaderPoints.Volume);
        Assert.IsNotNull(point);
        Assert.IsNull(point.OwnerPluginId, "the loader point is host-owned, like the filter point");
        Assert.AreEqual(1, point.Count);
        Assert.AreSame(SourceLoaderPlugin.Loader, point.Items[0]);
    }
    [TestMethod]
    public void DuplicateLoaderId_IsRejected()
    {
        var adapter = new TestHostAdapter(TestVolumes.Small());
        using var manager = new PluginManager(adapter);
        Assert.IsNotNull(manager.LoadInProcess(new SourceLoaderPlugin(), "test.dicom"));
        Assert.IsNull(manager.LoadInProcess(new SourceLoaderPlugin(), "test.dicom.dup"));
        var point = manager.PointRegistry.GetPoint<VolumeLoader>(LoaderPoints.Volume);
        Assert.IsNotNull(point);
        Assert.AreEqual(1, point.Count, "the failed registration must leave nothing behind");
    }
    [TestMethod]
    public void Unload_RemovesLoaderButKeepsPoint()
    {
        var adapter = new TestHostAdapter(TestVolumes.Small());
        using var manager = new PluginManager(adapter);
        manager.LoadInProcess(new SourceLoaderPlugin(), "test.dicom");
        var point = manager.PointRegistry.GetPoint<VolumeLoader>(LoaderPoints.Volume);
        Assert.IsNotNull(point);
        Assert.IsTrue(manager.Unload("test.dicom"));
        Assert.AreEqual(0, point.Count);
        Assert.IsNotNull(manager.PointRegistry.GetPoint<VolumeLoader>(LoaderPoints.Volume));
    }
    /// <summary>
    /// The contract that matters: slice z is handed over as a [Y, X] plane and ends up at z, which is
    /// what a format reader has to satisfy and what the host pipeline relies on.
    /// </summary>
    [TestMethod]
    public void Slices_BecomeTheVolume()
    {
        using var scratch = new TestDirectory();
        var source = SourceLoaderPlugin.Loader.Open(new VolumeLoadRequest { Paths = [scratch.Path] });
        Assert.IsNotNull(source);
        Assert.AreEqual(SourceLoaderPlugin.Depth, source.Depth);
        var volume = new Big3DArray<byte>(source.Width, source.Height, source.Depth);
        for (var z = 0; z < source.Depth; z++)
            volume.SetXYSlice(z, (byte[,])source.ReadSlice(z));
        for (var z = 0; z < volume.Z; z++)
        {
            for (var y = 0; y < volume.Y; y++)
            {
                for (var x = 0; x < volume.X; x++)
                    Assert.AreEqual((byte)z, volume[x, y, z], $"voxel ({x},{y},{z})");
            }
        }
    }
    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "layeron-test-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() => System.IO.Directory.Delete(Path, recursive: true);
    }
}
/// <summary>Contributes a custom data source through the first-class registry method.</summary>
[Plugin("test.dicom", Name = "Source loader")]
internal sealed class SourceLoaderPlugin : IPlugin
{
    public const int Width = 5;
    public const int Height = 4;
    public const int Depth = 3;
    /// <summary>A source needing no file: slice z is filled with z.</summary>
    public static readonly VolumeLoader Loader = new()
    {
        Id = "test.loader.synthetic",
        DisplayName = "Synthetic slices",
        Open = _ => new SyntheticSource()
    };
    public void Activate(IPluginContext context) => context.Registry.AddLoader(Loader);
    private sealed class SyntheticSource : SliceSource<byte>
    {
        public override int Width => SourceLoaderPlugin.Width;
        public override int Height => SourceLoaderPlugin.Height;
        public override int Depth => SourceLoaderPlugin.Depth;
        public override byte[,] GetSlice(int z)
        {
            var slice = new byte[SourceLoaderPlugin.Height, SourceLoaderPlugin.Width];
            for (var y = 0; y < SourceLoaderPlugin.Height; y++)
            {
                for (var x = 0; x < SourceLoaderPlugin.Width; x++)
                    slice[y, x] = (byte)z;
            }
            return slice;
        }
    }
}