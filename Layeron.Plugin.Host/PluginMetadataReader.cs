using System.Reflection;

namespace Layeron.Plugin.Host;

/// <summary>
/// Reads a plugin's identity out of the assembly metadata without executing any plugin code.
/// <para>
/// The values come from <c>&lt;AssemblyMetadata Include="LayeronPluginId" Value="..." /&gt;</c> entries
/// in the plugin project, which end up as real <see cref="AssemblyMetadataAttribute"/> rows in the
/// assembly metadata table. Loading the file with <see cref="MetadataLoadContext"/> reads those rows
/// as data only: no module initialiser, no static constructor and no dependency on the plugin's own
/// contracts, so a broken or hostile DLL is rejected before it ever runs.
/// </para>
/// </summary>
internal static class PluginMetadataReader
{
    /// <summary>Reads the declared metadata, or null when the file is not a readable managed assembly.</summary>
    public static PluginMetadata? TryRead(string assemblyPath)
    {
        try
        {
            using var context = new MetadataLoadContext(new PathAssemblyResolver(BuildProbingPaths(assemblyPath)));
            var assembly = context.LoadFromAssemblyPath(assemblyPath);
            var values = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var attribute in assembly.GetCustomAttributesData())
            {
                if (attribute.AttributeType.FullName != typeof(AssemblyMetadataAttribute).FullName)
                    continue;
                if (attribute.ConstructorArguments.Count != 2)
                    continue;

                if (attribute.ConstructorArguments[0].Value is string key &&
                    attribute.ConstructorArguments[1].Value is string value)
                {
                    values[key] = value;
                }
            }

            return new PluginMetadata
            {
                Id = values.GetValueOrDefault(Abstractions.PluginMetadataKeys.Id),
                Name = values.GetValueOrDefault(Abstractions.PluginMetadataKeys.Name),
                Version = values.GetValueOrDefault(Abstractions.PluginMetadataKeys.Version),
                Description = values.GetValueOrDefault(Abstractions.PluginMetadataKeys.Description),
                ApiVersion = values.GetValueOrDefault(Abstractions.PluginMetadataKeys.ApiVersion),
                AssemblyPath = assemblyPath
            };
        }
        catch
        {
            // Not a managed assembly, or a dependency could not be resolved for metadata reading.
            // Callers fall back to loading the assembly and reading [Plugin] instead.
            return null;
        }
    }

    /// <summary>
    /// Probing paths for metadata resolution: the runtime, the host folder and the plugin folder.
    /// MetadataLoadContext needs to resolve the assembly's references to materialise attribute
    /// types, and a plugin's private dependencies only exist next to it.
    /// </summary>
    private static IEnumerable<string> BuildProbingPaths(string assemblyPath)
    {
        var directories = new[]
        {
            Path.GetDirectoryName(assemblyPath),
            AppContext.BaseDirectory,
            Path.GetDirectoryName(typeof(object).Assembly.Location)
        };

        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in directories)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                continue;

            foreach (var file in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(file);
                seen.TryAdd(name, file);
            }
        }

        return seen.Values;
    }
}

/// <summary>Identity read from assembly metadata; every field is optional so partial metadata still loads.</summary>
internal sealed class PluginMetadata
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? Version { get; init; }
    public string? Description { get; init; }
    public string? ApiVersion { get; init; }
    public required string AssemblyPath { get; init; }
}
