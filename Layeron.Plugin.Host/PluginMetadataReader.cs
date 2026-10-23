using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Layeron.Plugin.Host;

/// <summary>
/// Reads a plugin's identity out of the assembly metadata without executing any plugin code.
/// <para>
/// The values come from <c>&lt;AssemblyMetadata Include="LayeronPluginId" Value="..." /&gt;</c> entries
/// in the plugin project, which end up as real <see cref="AssemblyMetadataAttribute"/> rows in the
/// assembly metadata table. Reading those rows is a pure metadata operation: no module initialiser, no
/// static constructor and no dependency on the plugin's own contracts, so a broken or hostile DLL is
/// rejected before it ever runs.
/// </para>
/// <para>
/// The rows are read with <see cref="MetadataReader"/> straight off the PE file rather than through
/// <see cref="MetadataLoadContext"/>. Both see the same table, but a load context has to materialise a
/// reflection object graph and resolve every referenced assembly, which made discovery the largest
/// single cost of a cold start: a plugin folder full of private dependencies was ~25 ms per non-plugin
/// DLL. Reading rows is an order of magnitude cheaper and needs no probing paths at all, because an
/// <c>AssemblyMetadataAttribute</c> is stored as literal strings.
/// </para>
/// </summary>
internal static class PluginMetadataReader
{
    /// <summary>Reads the declared metadata, or null when the file is not a readable managed assembly.</summary>
    public static PluginMetadata? TryRead(string assemblyPath)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);

            // Native libraries and resource-only files in a plugin folder are rejected here, before
            // any metadata is touched. They are the common case in a folder that ships dependencies.
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

            // No AssemblyMetadata rows means no declared plugin identity, which is what a private
            // dependency looks like. Returning null here lets the caller skip it without logging.
            if (values is null)
                return null;

            return new PluginMetadata
            {
                Id = values.GetValueOrDefault(Abstractions.PluginMetadataKeys.Id),
                Name = values.GetValueOrDefault(Abstractions.PluginMetadataKeys.Name),
                Version = values.GetValueOrDefault(Abstractions.PluginMetadataKeys.Version),
                Description = values.GetValueOrDefault(Abstractions.PluginMetadataKeys.Description),
                ApiVersion = values.GetValueOrDefault(Abstractions.PluginMetadataKeys.ApiVersion),
                LifetimeStyle = values.GetValueOrDefault(Abstractions.PluginMetadataKeys.LivetimeStyle),
                AssemblyPath = assemblyPath
            };
        }
        catch
        {
            // Not a managed assembly, or a malformed one. Callers treat this as "not a plugin".
            return null;
        }
    }

    /// <summary>True when the attribute is the assembly-level <c>System.Reflection.AssemblyMetadataAttribute</c>.</summary>
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

    /// <summary>
    /// Decodes the attribute's two string arguments. The blob starts with the fixed prolog
    /// <c>0x0001</c>; both arguments are serialized strings, so no custom-attribute decoding table is
    /// needed for this one attribute shape.
    /// </summary>
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

/// <summary>Identity read from assembly metadata; every field is optional so partial metadata still loads.</summary>
internal sealed class PluginMetadata
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? Version { get; init; }
    public string? Description { get; init; }
    public string? ApiVersion { get; init; }
    public required string AssemblyPath { get; init; }

    public string? LifetimeStyle { get; init; }
}
