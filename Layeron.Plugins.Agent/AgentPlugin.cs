using Layeron.Plugin.Abstractions;

namespace Layeron.Plugins.Agent;

/// <summary>
/// The plugin entry point: a chat tool panel, a conversation-history window and a settings page, all
/// created lazily by the host.
/// <para>
/// Activation touches no UI type and does no network work. The panel is built by the host's content factory
/// on first open, and its conversation is created on the first send - so a session that never opens the
/// panel never pays for an agent backend, and a machine with no provider configured still has a panel that
/// opens.
/// </para>
/// <para>
/// The one thing activation does do eagerly is read the options, because a settings page that shows values
/// before loading them would present the defaults as if they were the user's configuration.
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

    /// <summary>Tool id of the chat panel, unique inside this plugin.</summary>
    public const string ChatToolId = "agent.chat";

    /// <summary>Tool id of the conversation-history window, unique inside this plugin.</summary>
    public const string HistoryToolId = "agent.history";

    public void Activate(IPluginContext context)
    {
        var services = context.Services;

        // Options are restored from the plugin's data directory rather than kept in a field: the endpoint and
        // model are the kind of thing a user sets once, and re-typing them after every restart is the kind of
        // papercut that makes a panel feel like a demo.
        var options = new AgentOptions(context.PluginDataDirectory ?? context.PluginDirectory);

        // The options object is created once and shared: the settings page writes into it through the
        // host's declarative rows, and the chat panel reads the same instance on every send. That is why
        // the page takes the delegates rather than a snapshot of values.
        context.Registry.AddSettingsPage(options.CreateSettingsPage());

        // The transcript lives in a workspace, and the workspace that a chat belongs to is the one that was
        // active when the plugin loaded. Capturing it here also gives both tool windows one owner of "which
        // conversation is open", which is what lets the history window restore a chat into the chat panel.
        var scriptHost = services.GetService<Scripting.Abstractions.IScriptHostService>();
        var conversations = new AgentConversationManager(scriptHost?.WorkspaceRoot, services.Log);

        // Both windows are created lazily and need to open each other, so each holds a late-bound action.
        // By the time a content factory runs, activation has finished and the handles are assigned.
        Action? openHistory = null;

        // File attachments need the host's dialog, which arrives through the scripting seam - the host's
        // only UI-facing service. The history window does not need it; the chat panel does, and only when
        // it is constructed.
        var chat = context.Registry.AddTool(new ToolContribution
        {
            Id = ChatToolId,
            Title = "Agent",
            PreferredDock = DockHint.Right,
            ContentFactory = () => new AgentChatView(
                context,
                services,
                scriptHost ?? throw new InvalidOperationException("The host does not provide a scripting service, so the agent panel cannot be constructed."),
                options,
                conversations,
                () => openHistory?.Invoke())
        });

        // History is its own window: restoring a conversation is a management action, not something that
        // should take room away from the transcript or hide behind the "new conversation" button. Opening
        // a row reveals the chat panel, so the restore is visible instead of only changing a selection.
        var history = context.Registry.AddTool(new ToolContribution
        {
            Id = HistoryToolId,
            Title = "Agent 历史",
            PreferredDock = DockHint.Right,
            ContentFactory = () => new AgentHistoryView(conversations, chat.Open)
        });

        openHistory = history.Open;
    }
}

