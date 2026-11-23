using System.Linq;
using Layeron.Plugin.Abstractions;

namespace Layeron.Plugins.ModelSmooth;

/// <summary>
/// Contributes a smoothing panel that reads the 3D plugin's model list and writes smoothed models
/// back into it.
/// <para>
/// The plugin neither knows nor references the 3D plugin: it looks the model-list point up by id and
/// uses the contract types from the shared abstractions assembly. The algorithm, its parameters and
/// their UI all live here - the model plugin only lists models and turns contributed meshes into
/// scene nodes, so a second transform plugin with different parameters needs no change there.
/// </para>
/// <para>
/// The list is owned by the 3D plugin, so a session where it is not loaded has nothing to read.
/// Activation logs that and returns instead of failing, like any other optional extension consumer.
/// </para>
/// </summary>
[Plugin("layeron.modelsmooth")]
public sealed class ModelSmoothPlugin : IPlugin
{
    public void Activate(IPluginContext context)
    {
        var point = context.Registry.GetPoint<IModel3DModelList>(Model3DPoints.Models);
        var models = point?.Items.FirstOrDefault();

        if (models is null)
        {
            context.Services.Log.Info("3D 模型插件未加载，平滑面板未注册。");
            return;
        }

        context.Registry.AddTool(new ToolContribution
        {
            Id = "modelsmooth",
            Title = "模型平滑",
            PreferredDock = DockHint.Right,
            ContentFactory = () => new ModelSmoothView(models, context.Services)
        });
    }
}
