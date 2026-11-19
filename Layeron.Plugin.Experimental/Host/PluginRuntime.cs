namespace Layeron.Plugin.Experimental;

/// <summary>
/// Host-internal state for one active plugin activation.
/// <para>
/// This is deliberately not part of the public API. It binds the four things the host must own
/// together: the descriptor the activation came from, the plugin instance, the context that owns
/// its registrations, and the collectible load context when the plugin came from an assembly.
/// </para>
/// <para>
/// A reload ends one activation and starts another, so this is not a stable identity; the
/// descriptor is. Public callers see <see cref="PluginDescriptor"/> values, never this object.
/// </para>
/// </summary>
internal sealed class PluginRuntime
{
    public PluginRuntime(
        PluginDescriptor descriptor,
        IPlugin instance,
        PluginContext context,
        PluginLoadContext? loadContext,
        long activationOrder)
    {
        Descriptor = descriptor;
        Instance = instance;
        Context = context;
        LoadContext = loadContext;
        ActivationOrder = activationOrder;
    }

    public PluginDescriptor Descriptor { get; }

    public string Id => Descriptor.Id;

    public PluginLifetime Lifetime => Descriptor.Lifetime;

    public IPlugin Instance { get; }

    public PluginContext Context { get; }

    public PluginLoadContext? LoadContext { get; }

    /// <summary>Monotonic activation order; dependencies always have a lower value than dependents.</summary>
    public long ActivationOrder { get; }
}

