using Layeron.Plugin.Abstractions;

namespace Layeron.Plugins.MaskMorphology;

/// <summary>
/// Contributes a tool panel that applies binary morphology to mask layers - dilate, erode, open and
/// close - and a slice tool that extracts the connected mask component under a clicked seed. The
/// algorithms themselves have no UI dependency, so they can also be used by scripts or other plugins.
/// <para>
/// Activation registers the panel's lazy content factory and one lightweight slice tool. The panel's
/// view and view model are still created only when the user opens it.
/// </para>
/// </summary>
[Plugin("layeron.maskmorphology")]
public sealed class MaskMorphologyPlugin : IPlugin
{
    public void Activate(IPluginContext context)
    {
        var services = context.Services;

        context.Registry.AddTool(new ToolContribution
        {
            Id = "morphology",
            Title = "掩膜形态学",
            PreferredDock = DockHint.Right,
            ContentFactory = () => new MaskMorphologyView(services)
        });

        // Region growing is a slice interaction, so it belongs in the toolbox next to the brush and
        // magic wand rather than behind an Apply button: the user picks the seed by clicking the slice.
        context.Registry.AddSliceTool(new MaskRegionGrowTool(services));
    }
}
