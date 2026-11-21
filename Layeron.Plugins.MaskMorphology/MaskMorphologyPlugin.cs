using Layeron.Plugin.Abstractions;

namespace Layeron.Plugins.MaskMorphology;

/// <summary>
/// Contributes a tool panel that applies binary morphology to mask layers: dilate, erode, open,
/// close, and copy. The algorithm itself (<see cref="MaskMorphologyEngine"/>) has no UI dependency,
/// so it can also be used by scripts or other plugins.
/// <para>
/// Activation only registers a lazy content factory: no view, no view model and no work is created
/// until the user opens the panel.
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
    }
}
