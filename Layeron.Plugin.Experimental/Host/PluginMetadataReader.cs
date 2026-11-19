using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Layeron.Plugin.Experimental;

/// <summary>
/// Reads a plugin's identity and dependency declarations straight from assembly metadata, without
/// executing plugin code. This is what lets the host order plugins before any of them runs.
/// </summary>
internal static class PluginMetadataReader
{
    public static PluginMetadata? TryRead(string assemblyPath)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);

            if (!pe.HasMetadata)
                return null;

            var reader = pe.GetMetadataReader();
            if (!reader.IsAssembly)
                return null;

            Dictionary<string, string>? values = null;
            foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attribute = reader.GetCustomAttribute(handle);
                if (!IsAssemblyMetadataAttribute(reader, attribute))
                    continue;

                if (!TryReadPair(reader, attribute, out var key, out var value))
                    continue;

                values ??= new Dictionary<string, string>(StringComparer.Ordinal);
                values[key] = value;
            }

            var id = values?.GetValueOrDefault(PluginMetadataKeys.Id);
            if (values is null || string.IsNullOrWhiteSpace(id))
                return null;

            return new PluginMetadata
            {
                Id = id,
                Name = values.GetValueOrDefault(PluginMetadataKeys.Name) ?? id,
                Version = values.GetValueOrDefault(PluginMetadataKeys.Version) ?? "0.0.0",
                Description = values.GetValueOrDefault(PluginMetadataKeys.Description) ?? string.Empty,
                ApiVersion = values.GetValueOrDefault(PluginMetadataKeys.ApiVersion),
                Lifetime = ParseLifetime(values.GetValueOrDefault(PluginMetadataKeys.Lifetime)),
                Dependencies = ParseDependencies(values.GetValueOrDefault(PluginMetadataKeys.Dependencies)),
                AssemblyPath = assemblyPath
            };
        }
        catch
        {
            // Not a managed assembly or not readable. Discovery treats it as "not a plugin".
            return null;
        }
    }

    private static PluginLifetime ParseLifetime(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "app" or "application" => PluginLifetime.Application,
            _ => PluginLifetime.Reloadable
        };

    /// <summary>
    /// Parses <c>id[@minVersion];id[@minVersion]</c>. One semicolon-separated value keeps the
    /// metadata format small and readable; order is not significant because the host sorts the graph.
    /// </summary>
    private static IReadOnlyList<PluginDependency> ParseDependencies(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        var dependencies = new List<PluginDependency>();
        foreach (var entry in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf('@');
            if (separator < 0)
            {
                dependencies.Add(new PluginDependency(entry));
                continue;
            }

            var id = entry[..separator].Trim();
            var version = entry[(separator + 1)..].Trim();
            if (id.Length > 0)
                dependencies.Add(new PluginDependency(id, version.Length == 0 ? null : version));
        }

        return dependencies;
    }

    private static bool IsAssemblyMetadataAttribute(MetadataReader reader, CustomAttribute attribute)
    {
        if (attribute.Constructor.Kind != HandleKind.MemberReference)
            return false;

        var constructor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
        if (constructor.Parent.Kind != HandleKind.TypeReference)
            return false;

        var type = reader.GetTypeReference((TypeReferenceHandle)constructor.Parent);
        return reader.StringComparer.Equals(type.Namespace, "System.Reflection")
               && reader.StringComparer.Equals(type.Name, nameof(AssemblyMetadataAttribute));
    }

    private static bool TryReadPair(
        MetadataReader reader,
        CustomAttribute attribute,
        out string key,
        out string value)
    {
        key = string.Empty;
        value = string.Empty;

        var blob = reader.GetBlobReader(attribute.Value);
        if (blob.Length < 2 || blob.ReadUInt16() != 1)
            return false;

        var readKey = blob.ReadSerializedString();
        var readValue = blob.ReadSerializedString();
        if (readKey is null || readValue is null)
            return false;

        key = readKey;
        value = readValue;
        return true;
    }
}

internal sealed class PluginMetadata
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Version { get; init; }

    public required string Description { get; init; }

    public string? ApiVersion { get; init; }

    public required PluginLifetime Lifetime { get; init; }

    public required IReadOnlyList<PluginDependency> Dependencies { get; init; }

    public required string AssemblyPath { get; init; }
}

