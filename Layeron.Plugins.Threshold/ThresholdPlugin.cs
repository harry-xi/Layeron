using Layeron.Plugin.Abstractions;

namespace Layeron.Plugins.Threshold;

/// <summary>
/// The plugin entry point. It contributes a tool panel, and it publishes the extension point other
/// plugins feed to add buttons to its panel - a plugin that both extends the host and is extended by
/// other plugins.
/// <para>
/// Activation touches no UI type at all: the panel is created lazily by the host's content factory,
/// and the panel subscribes to the extension point itself. That keeps the plugin loadable in a
/// headless host (tests, batch tools) where the UI stack is absent, and it means the plugin holds no
/// reference to its own views, so there is no view registry to unwind when a panel closes.
/// </para>
/// <para>
/// There is no <c>Shutdown</c>: everything registered here is scoped to the context, so unloading the
/// plugin - or hot reloading it - is a single dispose.
/// </para>
/// </summary>
[Plugin("layeron.threshold")]
public sealed class ThresholdPlugin : IPlugin
{
    public void Activate(IPluginContext context)
    {
        var services = context.Services;

        var point = context.Registry.DefinePoint<VolumeAction>(
            ThresholdToolView.QuickActionsPointId,
            "Threshold panel extensions");

        context.Registry.AddTool(new ToolContribution
        {
            Id = "threshold",
            Title = "阈值分割",
            PreferredDock = DockHint.Right,
            ContentFactory = () => new ThresholdToolView(services, context.PluginId, point)
        });
    }
}
