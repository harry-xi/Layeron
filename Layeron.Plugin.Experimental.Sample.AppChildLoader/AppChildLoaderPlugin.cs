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
    private readonly IPluginContext _context;

    public AppChildLoaderPlugin(IPluginContext context) => _context = context;

    public void OnLoad() => _context.Registry.AddPluginSource(new ApplicationChildSource());

    public void Activate()
    {
    }

    public void OnEnable()
    {
    }

    public void OnHostShutdown()
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
            Factory = context => new ApplicationChildPlugin(context)
        };
    }
}

internal sealed class ApplicationChildPlugin : IApplicationPlugin
{
    public ApplicationChildPlugin(IPluginContext context) => _ = context;

    public void OnLoad()
    {
    }

    public void Activate()
    {
    }

    public void OnEnable()
    {
    }

    public void OnHostShutdown()
    {
    }
}
