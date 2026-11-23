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
/// <para>
/// The same manager is published outward as <see cref="IModel3DModelList"/> on
/// <see cref="Model3DPoints.Models"/>: other plugins read the current models and can add derived
/// ones (a smoothed model, say) without referencing this assembly. The list carries contract
/// types only; the panel remains the sole owner of the engine nodes.
/// </para>
/// </summary>
[Plugin("layeron.model3d")]
public sealed class Mask3DPlugin : IPlugin
{
    public void Activate(IPluginContext context)
    {
        var services = context.Services;

        // The model list is published outward as a read/write contract, so another plugin can read
        // the models that exist and add a derived one without referencing this plugin. The payload
        // type lives in the contract assembly; contributing this plugin's own namespace type would
        // give the consumer a second, incompatible copy.
        context.Registry.DefinePoint<IModel3DModelList>(Model3DPoints.Models, "3D 模型列表");

        var models = new Mask3DModelManager();

        // The generic argument is the contract interface, not the adapter: Contribute<T> looks the
        // point up by (id, T), and a point defined as IModel3DModelList cannot be fed with a
        // private implementation type.
        context.Registry.Contribute<IModel3DModelList>(Model3DPoints.Models, new Mask3DModelList(models));

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
