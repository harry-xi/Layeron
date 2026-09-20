using System;
using Layeron.App.ViewModels;

namespace Layeron.App.Layout;

/// <summary>
/// Identifies the concrete view-model type a dockable should restore to. The
/// values are stable strings (not CLR type names) so a moved or renamed CLR type
/// does not silently break saved layouts, and so plugin-provided windows have one
/// obvious registration point.
/// </summary>
public static class DockableIds
{
    public const string SliceAxial = "layeron.slice.axial";
    public const string SliceCoronal = "layeron.slice.coronal";
    public const string SliceSagittal = "layeron.slice.sagittal";
    public const string MaskManager = "layeron.tool.maskManager";
    public const string WindowLevel = "layeron.tool.windowLevel";
    public const string CodeEditor = "layeron.document.codeEditor";

    /// <summary>
    /// Type id for a tool contributed by a plugin. Stable across sessions and independent of the
    /// plugin's CLR type names, so a saved layout can reference a plugin panel and a missing plugin
    /// simply shows up as an unresolved dockable instead of breaking the layout.
    /// </summary>
    public static string ForPluginTool(string pluginId, string toolId) => $"layeron.tool.plugin.{pluginId}.{toolId}";

    /// <summary>True when the type id denotes a plugin-contributed tool.</summary>
    public static bool IsPluginTool(string typeId) =>
        typeId.StartsWith("layeron.tool.plugin.", StringComparison.Ordinal);

    /// <summary>Recovers the plugin id from a plugin tool type id, or null when it is not one.</summary>
    public static string? PluginIdOf(string typeId)
    {
        if (!IsPluginTool(typeId))
            return null;

        var rest = typeId["layeron.tool.plugin.".Length..];
        var lastDot = rest.LastIndexOf('.');
        return lastDot <= 0 ? null : rest[..lastDot];
    }

    public static string ForSliceAxis(SliceAxis axis) => axis switch
    {
        SliceAxis.Axial => SliceAxial,
        SliceAxis.Coronal => SliceCoronal,
        SliceAxis.Sagittal => SliceSagittal,
        _ => SliceAxial
    };
}