using Layeron.Plugin.Abstractions;
using Layeron.Scripting.Abstractions;

namespace Layeron.Plugins.ScriptHost;

/// <summary>
/// The C# script host. It contains no host-specific code path: it activates, registers a plugin source
/// and a script editor, and from then on the ordinary plugin manager discovers, activates, scopes and
/// unloads the scripts it offers - children of this plugin, so unloading this plugin unloads them.
/// <para>
/// The menu contributes only ways to get a script editor <em>window</em>; everything that acts on one
/// script (save, save as, run, run once) lives on that window's own toolbar, because those operations need
/// a subject and a menu has none. Reaching the host through <see cref="IScriptHostService"/> rather than
/// referencing the application is what keeps the two systems separate: the plugin owns the editor UI and
/// the scripting contract, the host owns the window, the storage provider and the plugin manager.
/// </para>
/// </summary>
[Plugin(Id, Name = "C# Script Host", Version = "1.0.0",
    Description = "Loads .csx scripts from the workspace as plugins and edits them in a dockable editor.")]
public sealed class ScriptHostPlugin : IPlugin
{
    /// <summary>Plugin id; must match the assembly metadata.</summary>
    public const string Id = "layeron.scripthost";

    private const string MenuPath = "脚本";

    public void Activate(IPluginContext context)
    {
        var host = context.Services.GetService<IScriptHostService>();

        // Registering a source is all it takes for scripts to become plugins: the manager calls Discover
        // and activates each script as a child scope of this plugin. The source scans the plugin
        // directories, so only installed scripts are activated - the user's workspace scripts are not.
        context.Registry.AddPluginSource(new ScriptPluginSource());

        var editor = new ScriptEditorController(context, host);
        context.Registry.Track(editor);

        // Registers a document per script up front, so a saved layout containing a script tab can restore
        // it. Re-running it after a workspace switch picks up that workspace's scripts as well.
        editor.RegisterExistingScripts();
        if (host is not null)
            context.Registry.Track(new Subscription(host, editor.RegisterExistingScripts));

        Menu(context, "新建脚本", () => editor.OpenNew());
        Menu(context, "打开脚本…", () => _ = editor.OpenFileAsync());
        Menu(context, "重新加载脚本", () => host?.ReloadAllScripts());
    }

    private static void Menu(IPluginContext context, string header, Action execute) =>
        context.Registry.AddMenu(new MenuContribution
        {
            Path = MenuPath,
            Header = header,
            Execute = execute
        });

    /// <summary>Ties an event subscription to the plugin scope, so unload unhooks it.</summary>
    private sealed class Subscription : IDisposable
    {
        private readonly IScriptHostService _host;
        private readonly Action _handler;
        private bool _disposed;

        public Subscription(IScriptHostService host, Action handler)
        {
            _host = host;
            _handler = handler;
            host.ScriptsDirectoryChanged += handler;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _host.ScriptsDirectoryChanged -= _handler;
        }
    }
}
