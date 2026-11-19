using Layeron.Plugin.Experimental;

namespace Layeron.Plugin.Experimental.Sample.BadLoader;

/// <summary>
/// A reloadable plugin that tries to act as a loader. Registering a source must fail because a
/// loader's child set would be tied to a plugin that can disappear at any moment.
/// </summary>
[Plugin("bad.loader")]
public sealed class BadLoaderPlugin : IPlugin
{
    public void Activate(IPluginContext context)
    {
        context.Services.Log.Info($"{context.PluginId}:Activate");
        context.Registry.AddPluginSource(new RejectedSource());
    }
}

internal sealed class RejectedSource : IPluginSource
{
    public string Id => "bad.loader.children";

    public string? Description => "Never registered: only application plugins may act as loaders.";

    public IEnumerable<PluginDescriptor> Discover(IPluginSourceContext context) => [];
}
