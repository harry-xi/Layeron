using Layeron.Plugin.Abstractions;

namespace Layeron.Plugins.ScriptHost;

/// <summary>
/// The object a script sees as <c>this</c>/globals. It is the only API surface scripts get, and it is
/// deliberately small: a script is a plugin, so what it can do is exactly what
/// <see cref="IPluginContext"/> allows - contribute UI, hook the volume, publish previews and feed
/// other plugins' extension points.
/// <para>
/// Scripts therefore extend the application <em>and</em> other plugins with no host support at all:
/// this plugin is an ordinary plugin that registered an <see cref="IPluginSource"/>.
/// </para>
/// </summary>
public sealed class ScriptGlobals
{
    public ScriptGlobals(IPluginContext context, string scriptPath)
    {
        Plugin = context;
        ScriptPath = scriptPath;
    }

    /// <summary>The script's plugin context: registrations made here unload with the script.</summary>
    public IPluginContext Plugin { get; }

    /// <summary>Path of the running script, handy for log messages.</summary>
    public string ScriptPath { get; }

    /// <summary>Host services (volume, masks, preview, logging).</summary>
    public IPluginServices Host => Plugin.Services;

    /// <summary>Writes a line to the host log, prefixed with the script name.</summary>
    public void Log(string message) =>
        Plugin.Services.Log.Info($"[{System.IO.Path.GetFileName(ScriptPath)}] {message}");

    /// <summary>Adds a menu entry under the scripts menu.</summary>
    public void Menu(string header, Action execute, Func<bool>? canExecute = null) =>
        Plugin.Registry.AddMenu(new MenuContribution
        {
            Path = "插件/脚本",
            Header = header,
            Execute = execute,
            CanExecute = canExecute
        });

    /// <summary>Adds a dockable panel built by the script.</summary>
    public void Tool(string id, string title, Func<object> contentFactory) =>
        Plugin.Registry.AddTool(new ToolContribution
        {
            Id = id,
            Title = title,
            ContentFactory = contentFactory
        });

    /// <summary>
    /// Contributes a button to an extension point defined by another plugin - the reason a script can
    /// extend a compiled plugin, and vice versa.
    /// </summary>
    public void Contribute(string pointId, VolumeAction action) =>
        Plugin.Registry.Contribute(pointId, action);
}
