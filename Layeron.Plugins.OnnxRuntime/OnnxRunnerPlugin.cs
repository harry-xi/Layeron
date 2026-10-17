using Layeron.Plugin.Abstractions;
using Layeron.Plugins.Onnx.Api;
using Layeron.Plugins.Onnx.Runtime;
using Layeron.Scripting.Abstractions;

namespace Layeron.Plugins.Onnx;

/// <summary>
/// Runs ONNX models as volume filters: it owns a model folder, reads each model's signature, binds Layeron data to
/// it, and shows the result as a preview overlay or a new mask or volume.
/// <para>
/// It contributes a tool panel and a settings page, and it publishes its runner through
/// <see cref="IOnnxRunnerService"/> so another plugin can execute a model without ever referencing ONNX Runtime.
/// </para>
/// <para>
/// Nothing is loaded at activation. Opening a model costs a native session and the host activates plugins on the
/// UI thread, so the first scan happens when the panel is opened - never before, and never on the startup path.
/// </para>
/// <para>
/// The two bundled demo models are embedded resources loaded straight into a session, so a first run has something
/// to run without deployment, without a file on disk, and without the plugin owning a folder it would then have to
/// keep in sync.
/// </para>
/// </summary>
[Plugin("layeron.onnxruntime")]
public sealed class OnnxRunnerPlugin : IPlugin
{
    private static readonly string[] MenuPath = ["ONNX"];

    public void Activate(IPluginContext context)
    {
        var services = context.Services;
        var dataDirectory = context.PluginDataDirectory ?? context.PluginDirectory ?? AppContext.BaseDirectory;
        var host = services.GetService<IScriptHostService>();

        var settings = new OnnxSettings(dataDirectory);
        var store = new OnnxModelStore();
        context.Registry.Track(store);

        // Any model files the plugin itself ships. Nothing is deployed anywhere: the folder is just another source
        // on the same scan path as the user's own folders, and the bundled demos come from memory instead.
        settings.PluginModelsFolder = Path.Combine(
            context.PluginDirectory ?? dataDirectory,
            OnnxModelStore.ModelsFolderName);

        var runner = new OnnxRunnerService(store, settings, services.Volumes)
        {
            PluginDataDirectory = dataDirectory,
            WorkspaceRoot = host?.WorkspaceRoot
        };
        context.Registry.Track(runner);

        // A workspace switch changes which models are in scope, so the folders are re-derived and the list
        // refreshed. The subscription is tracked, so unload unhooks it like everything else.
        if (host is not null)
        {
            context.Registry.Track(new WorkspaceSubscription(host, () =>
            {
                runner.WorkspaceRoot = host.WorkspaceRoot;
                _ = runner.RescanAsync();
            }));
        }

        // The scan starts here rather than when the panel opens, because the runner is a published service: another
        // plugin may ask for a model before any panel exists, and a service that reports an empty list until a user
        // clicks something would be lying. It runs on the runner's own lane, so activation returns immediately and
        // the UI thread is not blocked by a native session being created.
        RecordScan(runner, settings, dataDirectory, services.Log);

        var panel = context.Registry.AddTool(new ToolContribution
        {
            Id = "onnx.panel",
            Title = "ONNX",
            PreferredDock = DockHint.Right,
            ContentFactory = () => new OnnxToolView(services, runner, settings)
        });
        context.Registry.Track(panel);

        Menu(context, "打开面板", panel.Open);
        Menu(context, "重新扫描模型", () => _ = runner.RescanAsync());

        // A declarative page: the host renders the rows, so this plugin ships no settings UI at all. The model list
        // and per-model bindings are deliberately not settings - they belong to the panel, where the model they
        // apply to is in front of the user.
        context.Registry.Track(context.Registry.AddSettingsPage(new SettingsPageContribution
        {
            Id = "layeron.onnxruntime.settings",
            Title = "ONNX",
            Order = 70,
            Tooltip = "模型目录与内置示例模型",
            ValueGetter = settings.Get,
            ValueSetter = settings.Set,
            Settings =
            [
                new SettingItem
                {
                    Key = OnnxSettings.Keys.ExtraFolders,
                    Title = "额外模型目录",
                    Description = "以分号分隔。排在最前的目录优先，可用它覆盖某个内置示例模型。",
                    Kind = SettingKind.Text,
                    DefaultValue = string.Empty
                }
            ]
        }));

        // Published the way the Python runner publishes its service: on a point owned by this plugin, carrying a
        // contract type from a shared assembly, so a consumer resolves it by id and needs none of this plugin's code.
        // Because this plugin owns the point, unloading it removes the point too, so a consumer must handle a null
        // lookup as "the runner is not loaded" rather than expect an empty point.
        context.Registry.DefinePoint<IOnnxRunnerService>(IOnnxRunnerService.PointId, "ONNX runner service");
        context.Registry.Contribute<IOnnxRunnerService>(IOnnxRunnerService.PointId, runner);
    }

    private static void Menu(IPluginContext context, string header, Action execute) =>
        context.Registry.AddMenu(new MenuContribution
        {
            Path = MenuPath,
            Header = header,
            Execute = execute
        });

    /// <summary>
    /// Starts the first scan and reports what it found. The reporting is not chatter: a model that fails to load is
    /// otherwise invisible - the panel would simply show an empty list, with nothing anywhere saying that the folder
    /// was empty, the file was rejected, or the native runtime could not be resolved.
    /// </summary>
    private static void RecordScan(OnnxRunnerService runner, OnnxSettings settings, string dataDirectory, IPluginLog log)
    {
        var folders = settings.ModelFolders(dataDirectory, runner.WorkspaceRoot);
        _ = ReportAsync(runner, folders, log);
    }

    /// <summary>Waits for the first scan and reports it; see <see cref="RecordScan"/> for why it is reported at all.</summary>
    private static async Task ReportAsync(OnnxRunnerService runner, IReadOnlyList<string> folders, IPluginLog log)
    {
        try
        {
            await runner.RescanAsync().ConfigureAwait(false);

            var models = runner.Models;
            if (models.Count == 0)
            {
                log.Warn("未找到任何 ONNX 模型。已扫描：{Folders}", string.Join("; ", folders));
            }
            else
            {
                log.Info("已加载 {Count} 个 ONNX 模型：{Models}",
                    models.Count,
                    string.Join(", ", models.Select(m => m.Id)));
            }

            foreach (var (path, reason) in runner.Failures)
                log.Warn("ONNX 模型加载失败：{Path} — {Reason}", path, reason);
        }
        catch (Exception ex)
        {
            log.Error("ONNX 模型扫描失败。", ex);
        }
    }

    /// <summary>Ties an event subscription to the plugin scope, so unload unhooks it.</summary>
    private sealed class WorkspaceSubscription : IDisposable
    {
        private readonly IScriptHostService _host;
        private readonly Action _handler;
        private bool _disposed;

        public WorkspaceSubscription(IScriptHostService host, Action handler)
        {
            _host = host;
            _handler = handler;
            host.WorkspaceChanged += handler;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _host.WorkspaceChanged -= _handler;
        }
    }
}
