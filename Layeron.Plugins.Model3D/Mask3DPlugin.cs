using Layeron.Plugin.Abstractions;

namespace Layeron.Plugins.Model3D;

/// <summary>
/// Turns mask layers into 3D models.
/// <para>
/// The plugin contributes two surfaces that share one <see cref="Mask3DModelManager"/>: a tool panel
/// where models are created, imported and managed, and a document tab that only renders what the
/// manager holds. That split keeps the primary view usable as a long-lived comparison view while the
/// side panel navigates the collection - the same shape the rest of the application uses for data
/// (manager views) and views (slice tabs).
/// </para>
/// <para>
/// Nothing is computed at activation time - both contents are created lazily by the host's content
/// factories, so a session that never opens them never pays for the 3D engine, and the plugin stays
/// loadable in a headless host where the engine could not start at all.
/// </para>
/// </summary>
[Plugin("layeron.model3d")]
public sealed class Mask3DPlugin : IPlugin
{
    public void Activate(IPluginContext context)
    {
        var services = context.Services;
        var models = new Mask3DModelManager();

        // The document is registered first so the panel's "打开 3D 视图" can reveal it. The handle is
        // all the manager needs: it never learns the host's dockable ids.
        var view = context.Registry.AddDocument(new DocumentContribution
        {
            Id = "mask3d",
            Title = "掩膜 3D 模型",
            ContentFactory = () => new Mask3DView(models, services)
        });

        context.Registry.AddTool(new ToolContribution
        {
            Id = "mask3d.models",
            Title = "3D 模型",
            PreferredDock = DockHint.Right,
            ContentFactory = () => new Mask3DToolView(services, models)
        });

        // Revealing on the first model makes the result visible without pulling focus away from the
        // panel on every subsequent generation.
        models.RevealView = view.Open;
    }
}
