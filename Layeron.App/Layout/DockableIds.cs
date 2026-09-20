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

    public static string ForSliceAxis(SliceAxis axis) => axis switch
    {
        SliceAxis.Axial => SliceAxial,
        SliceAxis.Coronal => SliceCoronal,
        SliceAxis.Sagittal => SliceSagittal,
        _ => SliceAxial
    };
}