using Layeron.Plugin.Abstractions;
using System.Numerics;
using Layeron.Big3DArray;

namespace Layeron.Plugin.Test;

/// <summary>Extension point id used across the tests.</summary>
internal static class TestPoints
{
    public const string Actions = "test.point.actions";
}

/// <summary>
/// Defines an extension point and contributes to it. Used to prove that points are shared across
/// plugins, that contributions survive the contributor's own unload rules, and that a point defined by
/// a plugin disappears with it.
/// </summary>
[Plugin("test.publisher", Name = "Publisher")]
internal sealed class PublisherPlugin : IPlugin
{
    public static readonly List<string> Seen = [];

    public void Activate(IPluginContext context)
    {
        context.Registry.DefinePoint<VolumeAction>(TestPoints.Actions, "Test actions");
        context.Registry.AddMenu(new MenuContribution
        {
            Path = "Test/Publisher",
            Header = "From publisher",
            Execute = () => Seen.Add("publisher")
        });
    }
}

/// <summary>Contributes to a point another plugin defined, and registers a tool of its own.</summary>
[Plugin("test.consumer", Name = "Consumer")]
internal sealed class ConsumerPlugin : IPlugin
{
    public void Activate(IPluginContext context)
    {
        context.Registry.Contribute(TestPoints.Actions, new VolumeAction
        {
            Header = "Consumer action",
            Execute = services => services.Log.Info("consumer ran")
        });

        context.Registry.AddTool(new ToolContribution
        {
            Id = "consumer-tool",
            Title = "Consumer Tool",
            ContentFactory = () => new object()
        });
    }
}

/// <summary>
/// A plugin that is itself a plugin loader. It registers a source offering one in-process plugin, which
/// is how the tests exercise nested loading without depending on an external format.
/// </summary>
[Plugin("test.loader", Name = "Loader")]
internal sealed class LoaderPlugin : IPlugin
{
    public const string ChildId = "test.loader.child";

    public const string SourceId = "test.loader.child-source";

    public static readonly ManualResetEventSlim ChildActivated = new(false);

    public void Activate(IPluginContext context)
    {
        context.Registry.AddPluginSource(new ConstantPluginSource(SourceId, ChildId, () => new ChildPlugin()));
    }
}

/// <summary>The plugin a <see cref="LoaderPlugin"/> provides.</summary>
internal sealed class ChildPlugin : IPlugin
{
    public void Activate(IPluginContext context)
    {
        context.Registry.AddMenu(new MenuContribution
        {
            Path = "Test/Child",
            Header = "From child",
            Execute = () => { }
        });

        LoaderPlugin.ChildActivated.Set();
    }
}

/// <summary>Source that always offers the same in-process plugin.</summary>
internal sealed class ConstantPluginSource(string sourceId, string pluginId, Func<IPlugin> factory) : IPluginSource
{
    public int DiscoverCount { get; private set; }

    public string Id { get; } = sourceId;

    public string? Description => "test source";

    public IEnumerable<PluginDescriptor> Discover(IPluginSourceContext context)
    {
        DiscoverCount++;
        yield return new PluginDescriptor
        {
            Id = pluginId,
            Name = pluginId,
            Origin = "test",
            Instance = factory()
        };
    }
}

/// <summary>Plugin that writes into a mask and publishes a preview, i.e. uses the data services.</summary>
[Plugin("test.producer", Name = "Producer")]
internal sealed class ProducerPlugin : IPlugin
{
    public void Activate(IPluginContext context)
    {
        var services = context.Services;
        var volume = services.Volumes.Current;
        if (volume == null)
            return;

        var mask = new Layeron.Big3DArray.Big3DBitMap(volume.X, volume.Y, volume.Z);
        services.Masks.Add("producer mask", mask, MaskColor.Green);
        services.Masks.NotifyContentChanged();
        services.Preview.Publish(new PreviewOverlay
        {
            OwnerId = context.PluginId,
            Plane = SlicePlane.XY,
            Index = 0,
            Mask = new bool[volume.Y, volume.X]
        });
    }
}

/// <summary>
/// A pass-through volume filter (copies <c>src</c> to <c>dst</c> inside the requested region
/// only) used to prove filter registration, host-owned point survival and unload removal.
/// </summary>
internal sealed class CopyFilter : IVolumeFilter
{
    public string Id => "test.copy";

    public string DisplayName => "Copy";

    public string? Description => "test filter";

    public void Run<T>(ReadonlyBig3DArray<T> src, Big3DArray<T> dst, FilterRegion region, FilterContext ctx)
        where T : unmanaged, INumber<T>
    {
        for (var z = region.Z0; z < region.Z0 + region.Z; z++)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();
            for (var y = region.Y0; y < region.Y0 + region.Y; y++)
                for (var x = region.X0; x < region.X0 + region.X; x++)
                    dst[x, y, z] = src[x, y, z];
        }
    }
}

/// <summary>Contributes a volume filter through the first-class registry method.</summary>
[Plugin("test.filter", Name = "Filter")]
internal sealed class FilterPlugin : IPlugin
{
    public static readonly CopyFilter Filter = new();

    public void Activate(IPluginContext context) => context.Registry.AddFilter(Filter);
}
