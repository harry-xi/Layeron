using System.Collections.Generic;
using Layeron.Plugin.Abstractions;

namespace Layeron.App.Tools.BuiltIn;

/// <summary>
/// The built-in tool set: host features that are always present, contributed to the
/// <see cref="ToolCatalog"/> directly instead of going through the plugin extension point.
/// Region tools (lasso, polygon, rectangle, ellipse, magic wand, magnetic lasso) join this
/// list in later phases.
/// </summary>
internal static class BuiltinTools
{
    public static IReadOnlyList<SliceToolContribution> All =>
    [
        new SliceToolContribution
        {
            Descriptor = BrushTool.SharedDescriptor,
            Factory = () => new BrushTool()
        },
        new SliceToolContribution
        {
            Descriptor = EraserTool.SharedDescriptor,
            Factory = () => new EraserTool()
        }
    ];
}
