using Layeron.Plugin.Abstractions;

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
    public static readonly List<string> Seen = new();

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

    public IEnumerable<PluginCandidate> Discover(IPluginSourceContext context)
    {
        DiscoverCount++;
        yield return new PluginCandidate
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
