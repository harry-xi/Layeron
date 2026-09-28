using Layeron.Plugin.Abstractions;

namespace Layeron.Plugin.Host;

/// <summary>
/// The host side of the plugin system: everything that depends on where plugins are shown.
/// <see cref="PluginManager"/> owns loading, lifetime and scoping; the application supplies
/// this adapter so the manager stays free of any UI framework.
/// <para>
/// <see cref="AttachTool"/>, <see cref="AttachDocument"/> and <see cref="AttachMenu"/> must return a
/// handle that removes the contribution; the plugin manager disposes those handles in reverse order
/// when the owning plugin (or the nested scope that registered it) unloads.
/// </para>
/// </summary>
public interface IPluginHostAdapter
{
    /// <summary>Services handed to every plugin: volume, masks, preview channel and logging.</summary>
    IPluginServices Services { get; }

    /// <summary>Log sink for host-level messages (discovery, load, unload).</summary>
    IPluginLog Log { get; }

    /// <summary>Attaches a contributed tool panel and returns its removal handle.</summary>
    IDisposable AttachTool(string pluginId, ToolContribution tool);

    /// <summary>Attaches a contributed document tab and returns its removal handle.</summary>
    IDisposable AttachDocument(string pluginId, DocumentContribution document);

    /// <summary>Attaches a contributed menu entry and returns its removal handle.</summary>
    IDisposable AttachMenu(string pluginId, MenuContribution menu);
}
