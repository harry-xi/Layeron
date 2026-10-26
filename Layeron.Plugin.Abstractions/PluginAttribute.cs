namespace Layeron.Plugin.Abstractions;

/// <summary>
/// Marks the single entry class of a plugin. The class must implement <see cref="IPlugin"/>
/// and have a public parameterless constructor.
/// <para>
/// This attribute is the plugin's in-assembly identity. Identity that the host needs
/// <em>before</em> executing any plugin code (id, name, version, required API version) is
/// emitted as assembly metadata instead - see <see cref="PluginMetadataKeys"/> - so the host
/// can validate a DLL without loading it into the process.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class PluginAttribute(string id) : Attribute
{
    /// <summary>Stable, unique plugin id (e.g. <c>layeron.threshold</c>).</summary>
    public string Id { get; } = id;
}

/// <summary>
/// Well-known assembly metadata keys used by .NET plugins. They are declared in the plugin's
/// project file as <c>&lt;AssemblyMetadata Include="LayeronPluginId" Value="..." /&gt;</c> and are
/// readable by the host through the assembly's metadata tables without running plugin code.
/// <para>
/// Non-.NET plugins (scripts, interpreters) have no assembly metadata. They are discovered by an
/// <see cref="IPluginSource"/>, which creates their <see cref="PluginDescriptor.Instance"/> itself;
/// the host does not read a manifest on its own.
/// </para>
/// </summary>
public static class PluginMetadataKeys
{
    public const string Id = "LayeronPluginId";
    public const string Name = "LayeronPluginName";
    public const string Version = "LayeronPluginVersion";
    public const string Description = "LayeronPluginDescription";
    public const string ApiVersion = "LayeronPluginApiVersion";

    public const string LivetimeStyle = "LayeronPluginLifetime";
}

/// <summary>
/// Version of the plugin contract this assembly implements. Assets declare the version they
/// were built against; the host refuses candidates whose major version differs.
/// </summary>
public static class PluginApi
{
    public const string Version = "1.0";
}
