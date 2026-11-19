using Layeron.Plugin.Experimental;

namespace Layeron.Plugin.Experimental.Sample.Points;

/// <summary>
/// The plugin owns its contract type. The host never references this assembly; it inspects the
/// points through the non-generic <c>IExtensionPoint</c> view instead.
/// </summary>
[ExtensionPointId("experimental.demo.notes")]
public sealed record DemoNote(string Text, string SourcePluginId);

/// <summary>
/// Defines two independent points for the same contribution contract. This is the uncommon case
/// that still needs a discriminator; no string is passed at the common call sites.
/// </summary>
[Plugin("sample.points")]
public sealed class SamplePointsPlugin : IPlugin
{
    public void Activate(IPluginContext context)
    {
        context.Registry.DefinePoint<DemoNote>("left", "Left notes");
        context.Registry.DefinePoint<DemoNote>("right", "Right notes");

        context.Registry.Contribute(new DemoNote("left", context.PluginId), "left");
        context.Registry.Contribute(new DemoNote("right", context.PluginId), "right");
    }
}
