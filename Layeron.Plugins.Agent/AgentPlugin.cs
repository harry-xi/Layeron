using Layeron.Plugin.Abstractions;

namespace Layeron.Plugins.Agent;

/// <summary>
/// The plugin entry point: one chat tool panel and one settings page, both created lazily by the host.
/// <para>
/// Activation touches no UI type and does no network work. The panel is built by the host's content factory
/// on first open, and its conversation is created on the first send - so a session that never opens the
/// panel never pays for an agent backend, and a machine with no provider configured still has a panel that
/// opens.
/// </para>
/// <para>
/// There is no <c>Shutdown</c>: everything registered here is scoped to the context, so unloading - or hot
/// reloading - the plugin is a single dispose.
/// </para>
/// </summary>
[Plugin(PluginId)]
public sealed class AgentPlugin : IPlugin
{
    public const string PluginId = "layeron.agent";

    /// <summary>Tool id, unique inside this plugin.</summary>
    public const string ChatToolId = "agent.chat";

    public void Activate(IPluginContext context)
    {
        var services = context.Services;
        var options = new AgentOptions();

        // The options object is created once and shared: the settings page writes into it through the
        // host's declarative rows, and the chat panel reads the same instance on every send. That is why
        // the page takes the delegates rather than a snapshot of values.
        context.Registry.AddSettingsPage(options.CreateSettingsPage());

        context.Registry.AddTool(new ToolContribution
        {
            Id = ChatToolId,
            Title = "Agent",
            PreferredDock = DockHint.Right,
            // File attachments need the host's dialog, which arrives through the scripting seam - the
            // host's only UI-facing service. Absent it, the panel still works with path and text
            // attachments instead of failing to construct.
            ContentFactory = () => new AgentChatView(
                services,
                services.GetService<Scripting.Abstractions.IScriptHostService>(),
                options)
        });
    }
}

