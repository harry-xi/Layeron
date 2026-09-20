using Layeron.Plugin.Abstractions;

namespace Layeron.Plugins.ScriptHost;

/// <summary>
/// The script host plugin. It contains no host-specific code path: it activates, registers a plugin
/// source, and from then on the ordinary plugin manager discovers, activates, scopes and unloads the
/// scripts it offers - children of this plugin, so unloading this plugin unloads them.
/// </summary>
[Plugin(Id, Name = "C# Script Host", Version = "1.0.0",
    Description = "Loads .csx scripts from a folder as plugins.")]
public sealed class ScriptHostPlugin : IPlugin
{
    /// <summary>Plugin id; must match the assembly metadata.</summary>
    public const string Id = "layeron.scripthost";

    public void Activate(IPluginContext context)
    {
        // Registering a source is all it takes: the manager will call Discover and activate each
        // script as a child scope of this plugin.
        context.Registry.AddPluginSource(new ScriptPluginSource());

        context.Registry.AddMenu(new MenuContribution
        {
            Path = "插件/脚本",
            Header = "重新加载脚本",
            Execute = () => context.Services.Log.Info("Scripts are reloaded from the 插件 menu.")
        });
    }
}
