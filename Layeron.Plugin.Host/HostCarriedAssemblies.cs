using System.Reflection;
using System.Runtime.Loader;

namespace Layeron.Plugin.Host;

/// <summary>
/// The exact set of assemblies <em>this process</em> carries, which is the rule that decides whether a
/// plugin resolves a name from the host or from its own folder.
/// <para>
/// The set is the runtime's own trusted platform assemblies: the shared framework plus every runtime
/// asset in the host's dependency closure. That is deliberately a closed list rather than a name
/// pattern: a pattern cannot tell a host assembly from a plugin's private assembly, and the two cases
/// that matter both fall on the wrong side of one.
/// </para>
/// <list type="bullet">
/// <item>
///   A plugin-private dependency with a host-shaped name - <c>System.Numerics.Tensors</c>,
///   <c>Microsoft.CodeAnalysis</c> - looks like the host's by name and is not. Deferring it leaves the
///   name <em>permanently</em> unresolvable, because returning null hands the lookup to the default
///   context, which has no copy and does not fall back to the plugin's folder.
/// </item>
/// <item>
///   A host dependency that is not named after the framework - <c>NumSharp</c>, <c>Serilog</c>,
///   <c>fo-dicom.core</c> - looks like a plugin's by name and is not. Loading it privately gives one
///   type two identities, so a cast across the boundary fails with no error explaining why.
/// </item>
/// </list>
/// <para>
/// Membership therefore means "the default context can serve this name right now", which is exactly
/// the question <see cref="PluginLoadContext.Load"/> is asking. A name the process does not carry is
/// left to the plugin's own folder and its <c>.deps.json</c>, which is where a plugin's private
/// dependencies belong.
/// </para>
/// </summary>
internal static class HostCarriedAssemblies
{
    /// <summary>
    /// Resolved once: the closure cannot change while the process runs, and a plugin load must not pay
    /// for re-reading it. Built eagerly enough to be safe under concurrent plugin loads.
    /// </summary>
    private static readonly Lazy<HashSet<string>> Carried = new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// True when the process carries an assembly with this simple name, i.e. the default context can
    /// provide it and a plugin must bind to that copy rather than ship a second one.
    /// </summary>
    public static bool Contains(string? simpleName) =>
        !string.IsNullOrEmpty(simpleName) && Carried.Value.Contains(simpleName);

    /// <summary>The carried simple names, for diagnostics.</summary>
    public static IReadOnlyCollection<string> Names => Carried.Value;

    private static HashSet<string> Build()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string list)
        {
            foreach (var path in list.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                if (Path.GetFileNameWithoutExtension(path) is { Length: > 0 } name)
                    names.Add(name);
            }
        }

        // A host that never went through the SDK's dependency resolution has no such list. The default
        // context is then the only account of what the process carries, so fall back to what it holds.
        if (names.Count == 0)
        {
            foreach (var assembly in AssemblyLoadContext.Default.Assemblies)
            {
                if (assembly.GetName().Name is { Length: > 0 } name)
                    names.Add(name);
            }
        }

        // The host's own assembly is the one thing this rule must never get wrong, and it is the one
        // name that is always provable from inside this code.
        if (typeof(HostCarriedAssemblies).Assembly.GetName().Name is { Length: > 0 } host)
            names.Add(host);

        return names;
    }
}
