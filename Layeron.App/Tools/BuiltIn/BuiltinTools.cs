using System.Collections.Generic;
using Layeron.Plugin.Abstractions;

namespace Layeron.App.Tools.BuiltIn;

/// <summary>
/// The built-in tool set: host features that are always present, contributed to the
/// <see cref="ToolCatalog"/> directly instead of going through the plugin extension point.
/// </summary>
internal static class BuiltinTools
{
    public static IReadOnlyList<SliceToolContribution> All =>
    [
        new SliceToolContribution
        {
            Descriptor = LassoTool.SharedDescriptor,
            Factory = () => new LassoTool()
        },
        new SliceToolContribution
        {
            Descriptor = PolygonTool.SharedDescriptor,
            Factory = () => new PolygonTool()
        },
        new SliceToolContribution
        {
            Descriptor = RectangleTool.SharedDescriptor,
            Factory = () => new RectangleTool()
        },
        new SliceToolContribution
        {
            Descriptor = EllipseTool.SharedDescriptor,
            Factory = () => new EllipseTool()
        },
        new SliceToolContribution
        {
            Descriptor = MagicWandTool.SharedDescriptor,
            Factory = () => new MagicWandTool()
        },
        new SliceToolContribution
        {
            Descriptor = MagneticLassoTool.SharedDescriptor,
            Factory = () => new MagneticLassoTool()
        },
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
