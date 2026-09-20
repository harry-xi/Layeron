using System.Numerics;
using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;
using Layeron.Plugin.Host;

namespace Layeron.Plugin.Test;

/// <summary>
/// Compiles and exercises the plugin-author example that the documentation hands to users. Documentation
/// that does not compile is worse than none, so the sample lives here as real code and is checked by the
/// test below.
/// </summary>
[Plugin("test.docs-sample", Name = "Docs Sample", Version = "1.0.0")]
internal sealed class DocsSamplePlugin : IPlugin
{
    public void Activate(IPluginContext context)
    {
        context.Registry.AddMenu(new MenuContribution
        {
            Path = "插件/我的功能",
            Header = "统计体素数",
            Execute = () =>
            {
                var volume = context.Services.Volumes.Current;
                if (volume is null)
                    return;

                // 泛型方法只写一遍，byte / ushort 共用；派发只发生这一次。
                var total = volume switch
                {
                    ReadonlyBig3DArray<byte> v8 => Count(v8),
                    ReadonlyBig3DArray<ushort> v16 => Count(v16),
                    _ => 0L
                };

                context.Services.Log.Info($"voxels = {total}");
            }
        });
    }

    private static long Count<T>(ReadonlyBig3DArray<T> volume) where T : unmanaged, INumber<T>
    {
        long total = 0;

        for (var block = 0; block < volume.BlockCount; block++)
        {
            // ForEachInVolumeRange skips the padding of edge blocks, which holds no voxel data. An
            // interior block arrives as one range covering the whole block.
            volume.ForEachInVolumeRange(block, (offset, count) =>
            {
                var span = volume.GetBlockSpan(block);
                var end = offset + count;

                for (var i = offset; i < end; i++)
                    total += long.CreateChecked(span[i]);
            });
        }

        return total;
    }
}

[TestClass]
public sealed class DocumentationSampleTests
{
    [TestMethod]
    public void SamplePlugin_ActivatesAndRunsItsMenuAction()
    {
        var volume = new Big3DArray<ushort>(4, 4, 4);
        volume.Fill((_, _, _) => 3);

        var adapter = new TestHostAdapter();
        (adapter.Services.Volumes as TestVolumeService)!.Replace(volume);

        using var manager = new PluginManager(adapter);
        Assert.IsNotNull(manager.LoadInProcess(new DocsSamplePlugin(), "test.docs-sample"));

        var menu = adapter.Menus.Values.Single(m => m.Header == "统计体素数");
        Assert.IsTrue(menu.CanExecute?.Invoke() ?? true);
        menu.Execute();

        var expected = 4L * 4 * 4 * 3;
        var log = (CollectingLog)((TestServices)adapter.Services).Log;
        Assert.IsTrue(log.Contains($"voxels = {expected}"),
            $"the sample must report {expected} voxels; log was: {string.Join(" | ", log.Messages)}");
    }
}
