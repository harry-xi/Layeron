using Layeron.Plugin.Experimental;

namespace Layeron.Plugin.Experimental.Sample.AppChildLoader;

/// <summary>
/// An application plugin that is allowed to register a source, but whose source publishes an
/// application-lifetime child. The host must reject that child: application plugins belong to the
/// initial host startup, not to a loader's dynamic set.
/// </summary>
[Plugin("app.child.loader")]
public sealed class AppChildLoaderPlugin : IApplicationPlugin
{
    public void OnLoad(IPluginContext context) =>
        context.Registry.AddPluginSource(new ApplicationChildSource());

    public void Activate(IPluginContext context)
    {
    }

    public void OnEnable(IPluginContext context)
    {
    }

    public void OnHostShutdown(IPluginContext context)
    {
    }
}

internal sealed class ApplicationChildSource : IPluginSource
{
    public string Id => "app.child.loader.children";

    public string? Description => "Publishes an application-lifetime child.";

    public IEnumerable<PluginDescriptor> Discover(IPluginSourceContext context)
    {
        yield return new PluginDescriptor
        {
            Id = "child.app",
            Name = "Rejected Application Child",
            Version = "1.0.0",
            Description = "Source-discovered application plugin; the host must reject it.",
            ApiVersion = PluginApi.Version,
            Lifetime = PluginLifetime.Application,
            Factory = () => new ApplicationChildPlugin()
        };
    }
}

internal sealed class ApplicationChildPlugin : IApplicationPlugin
{
    public void OnLoad(IPluginContext context)
    {
    }

    public void Activate(IPluginContext context)
    {
    }

    public void OnEnable(IPluginContext context)
    {
    }

    public void OnHostShutdown(IPluginContext context)
    {
    }
}
