using Layeron.Plugin.Abstractions;

namespace Layeron.Plugins.Model3D;

/// <summary>
/// Turns a mask layer into an interactive 3D surface.
/// <para>
/// It contributes a <em>document</em> rather than a tool panel: the model is a primary view that
/// belongs next to the slice views, and it needs the room the centre dock gives it. Nothing is
/// computed at activation time - the tab is created lazily by the host's content factory, so a
/// session that never opens it never pays for the 3D engine, and the plugin stays loadable in a
/// headless host where the engine could not start at all.
/// </para>
/// </summary>
[Plugin("layeron.model3d")]
public sealed class Mask3DPlugin : IPlugin
{
    public void Activate(IPluginContext context)
    {
        var services = context.Services;

        context.Registry.AddDocument(new DocumentContribution
        {
            Id = "mask3d",
            Title = "掩膜 3D 模型",
            ContentFactory = () => new Mask3DView(services)
        });
    }
}
