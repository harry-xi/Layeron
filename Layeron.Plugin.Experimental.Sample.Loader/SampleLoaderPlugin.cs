using Layeron.Plugin.Experimental;

namespace Layeron.Plugin.Experimental.Sample.Loader;

/// <summary>
/// Application-lifetime loader sample. It registers a source in OnLoad; the source publishes two
/// child plugins into the host's normal dependency graph.
/// </summary>
[Plugin("sample.loader")]
public sealed class SampleLoaderPlugin : IApplicationPlugin
{
    private readonly IPluginContext _context;

    public SampleLoaderPlugin(IPluginContext context) => _context = context;

    public void OnLoad()
    {
        Record("OnLoad");
        _context.Registry.AddPluginSource(new SampleChildSource());
    }

    public void Activate() => Record("Activate");

    public void OnEnable() => Record("OnEnable");

    public void OnHostShutdown() => Record("OnHostShutdown");

    private void Record(string @event) =>
        _context.Services.Log.Info($"{_context.PluginId}:{@event}");
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
        Factory = context => new SampleChildPlugin(context)
    };
}

/// <summary>A child plugin owned by the loader and unloaded/reloaded as part of its subtree.</summary>
internal sealed class SampleChildPlugin(IPluginContext context) : IPlugin
{
    public void Activate()
    {
        context.Services.Log.Info($"{context.PluginId}:Activate");
        context.Registry.Track(new ActionDisposable(
            () => context.Services.Log.Info($"{context.PluginId}:Dispose")));
    }
}
