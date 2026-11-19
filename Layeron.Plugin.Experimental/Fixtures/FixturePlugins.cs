namespace Layeron.Plugin.Experimental;

// Test doubles for manager-level rules (ordering, dependency validation, lifecycle). They are
// deliberately in-process: the scenarios that use them assert how the manager schedules a graph,
// not how an assembly is discovered, so a DLL would add ceremony without adding coverage.
// Every plugin that demonstrates real loading lives in its own Sample.* project.

/// <summary>Observes its own scoped teardown to prove when a context is disposed.</summary>
internal sealed class TrackingPlugin(ScenarioHost trace, string pluginId) : IPlugin
{
    public void Activate(IPluginContext context)
    {
        trace.Record(pluginId, "Activate");
        context.Registry.Track(new ActionDisposable(() => trace.Record(pluginId, "Dispose")));
    }
}

/// <summary>Plugin whose activation is intentionally trivial; used where a graph never reaches it.</summary>
internal sealed class NoopPlugin : IPlugin
{
    public void Activate(IPluginContext context)
    {
    }
}

/// <summary>Application-lifetime fixture used to assert callback ordering.</summary>
internal sealed class TraceApplicationPlugin(ScenarioHost trace, string pluginId) : IApplicationPlugin
{
    public void OnLoad(IPluginContext context) => trace.Record(pluginId, "OnLoad");

    public void Activate(IPluginContext context) => trace.Record(pluginId, "Activate");

    public void OnEnable(IPluginContext context) => trace.Record(pluginId, "OnEnable");

    public void OnHostShutdown(IPluginContext context) => trace.Record(pluginId, "OnHostShutdown");
}

/// <summary>Application-lifetime fixture for invalid-graph scenarios.</summary>
internal sealed class NoopApplicationPlugin : IApplicationPlugin
{
    public void OnLoad(IPluginContext context)
    {
    }

    public void Activate(IPluginContext context)
    {
    }

    public void OnEnable(IPluginContext context)
    {
    }

    public void OnHostShutdown(IPluginContext context)
    {
    }
}
