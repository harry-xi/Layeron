namespace Layeron.Plugin.Experimental;

// Test doubles for manager-level rules (ordering, dependency validation, lifecycle). They are
// deliberately in-process: the scenarios that use them assert how the manager schedules a graph,
// not how an assembly is discovered, so a DLL would add ceremony without adding coverage.
// Every plugin that demonstrates real loading lives in its own Sample.* project.

/// <summary>Observes its own scoped teardown to prove when a context is disposed.</summary>
internal sealed class TrackingPlugin : IPlugin
{
    private readonly ScenarioHost _trace;
    private readonly string _pluginId;
    private readonly IPluginContext _context;

    public TrackingPlugin(ScenarioHost trace, string pluginId, IPluginContext context)
    {
        _trace = trace;
        _pluginId = pluginId;
        _context = context;
    }

    public void Activate()
    {
        _trace.Record(_pluginId, "Activate");
        _context.Registry.Track(new ActionDisposable(() => _trace.Record(_pluginId, "Dispose")));
    }
}

/// <summary>Plugin whose activation is intentionally trivial; used where a graph never reaches it.</summary>
internal sealed class NoopPlugin : IPlugin
{
    public NoopPlugin(IPluginContext context) => _ = context;

    public void Activate()
    {
    }
}

/// <summary>Application-lifetime fixture used to assert callback ordering.</summary>
internal sealed class TraceApplicationPlugin : IApplicationPlugin
{
    private readonly ScenarioHost _trace;
    private readonly string _pluginId;

    public TraceApplicationPlugin(ScenarioHost trace, string pluginId, IPluginContext context)
    {
        _trace = trace;
        _pluginId = pluginId;
        _ = context;
    }

    public void OnLoad() => _trace.Record(_pluginId, "OnLoad");

    public void Activate() => _trace.Record(_pluginId, "Activate");

    public void OnEnable() => _trace.Record(_pluginId, "OnEnable");

    public void OnHostShutdown() => _trace.Record(_pluginId, "OnHostShutdown");
}

/// <summary>Application-lifetime fixture for invalid-graph scenarios.</summary>
internal sealed class NoopApplicationPlugin : IApplicationPlugin
{
    public NoopApplicationPlugin(IPluginContext context) => _ = context;

    public void OnLoad()
    {
    }

    public void Activate()
    {
    }

    public void OnEnable()
    {
    }

    public void OnHostShutdown()
    {
    }
}

/// <summary>
/// Proves that the constructor receives the same context that owns the contribution scope. The
/// constructor only records that fact and stores nothing; registration stays in Activate, as the
/// API recommends.
/// </summary>
internal sealed class ConstructorProbePlugin : IPlugin
{
    private readonly ScenarioHost _trace;
    private readonly string _pluginId;
    private readonly IPluginContext _context;

    public ConstructorProbePlugin(ScenarioHost trace, IPluginContext context)
    {
        _trace = trace;
        _pluginId = context.PluginId;
        _context = context;

        trace.Record(_pluginId, "Constructed");
    }

    public void Activate()
    {
        _trace.Record(_pluginId, "Activate");
        _context.Registry.Track(new ActionDisposable(() => _trace.Record(_pluginId, "Dispose")));
    }
}

/// <summary>
/// Defensive fixture: a constructor is ordinary code, so it can register and then throw even though
/// the API tells plugins not to. The host must still roll the context back and report the real
/// exception.
/// </summary>
internal sealed class ConstructorFailurePlugin : IPlugin
{
    public ConstructorFailurePlugin(ScenarioHost trace, IPluginContext context)
    {
        trace.Record(context.PluginId, "Constructed");
        context.Registry.Track(new ActionDisposable(
            () => trace.Record(context.PluginId, "Dispose")));

        throw new InvalidOperationException("constructor failure");
    }

    public void Activate() => throw new InvalidOperationException("must not activate");
}
