using Layeron.Plugin.Experimental;

namespace Layeron.Plugin.Experimental.Sample.Loader;

/// <summary>
/// Application-lifetime loader sample. It registers a source in OnLoad; the source publishes two
/// child plugins into the host's normal dependency graph.
/// </summary>
[Plugin("sample.loader")]
public sealed class SampleLoaderPlugin : IApplicationPlugin
{
    public void OnLoad(IPluginContext context)
    {
        Record(context, "OnLoad");
        context.Registry.AddPluginSource(new SampleChildSource());
    }

    public void Activate(IPluginContext context) => Record(context, "Activate");

    public void OnEnable(IPluginContext context) => Record(context, "OnEnable");

    public void OnHostShutdown(IPluginContext context) => Record(context, "OnHostShutdown");

    private static void Record(IPluginContext context, string @event) =>
        context.Services.Log.Info($"{context.PluginId}:{@event}");
}

/// <summary>
/// The loader's discovery surface. A real loader would enumerate a folder, scripts or a feed;
/// the host only cares that the returned descriptors join the regular dependency graph.
/// </summary>
internal sealed class SampleChildSource : IPluginSource
{
    public string Id => "sample.loader.children";

    public string? Description => "Sample loader children";

    public IEnumerable<PluginDescriptor> Discover(IPluginSourceContext context)
    {
        yield return Child("child.one", "Sample Child One", "sample.loader");
        yield return Child("child.two", "Sample Child Two", "child.one");
    }

    private static PluginDescriptor Child(string id, string name, string dependency) => new()
    {
        Id = id,
        Name = name,
        Version = "1.0.0",
        Description = "Child plugin discovered by the sample loader.",
        ApiVersion = PluginApi.Version,
        Dependencies = [new PluginDependency(dependency)],
        Factory = () => new SampleChildPlugin()
    };
}

/// <summary>A child plugin owned by the loader and unloaded/reloaded as part of its subtree.</summary>
internal sealed class SampleChildPlugin : IPlugin
{
    public void Activate(IPluginContext context)
    {
        context.Services.Log.Info($"{context.PluginId}:Activate");
        context.Registry.Track(new ActionDisposable(
            () => context.Services.Log.Info($"{context.PluginId}:Dispose")));
    }
}
