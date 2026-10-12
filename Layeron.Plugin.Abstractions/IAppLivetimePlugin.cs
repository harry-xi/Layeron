namespace Layeron.Plugin.Abstractions;

public interface IAppLivetimePlugin : IPlugin
{
    /// <summary>
    /// Called when the host is starting up, before any other plugin is activated.
    /// </summary>
    void OnLoad(IPluginContext context);

    /// <summary>
    /// Called when the plugin is enabled. (call after all other App lifetime plugins have been loaded, but before any other plugin is activated)
    /// </summary>
    void OnEnable(IPluginContext context);

    /// <summary>
    /// Called when the host is shutting down, after all other plugins have been deactivated.
    /// </summary>
    void OnHostShutdown(IPluginContext context);
}