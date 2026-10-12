using Avalonia.Threading;
using Layeron.Plugin.Abstractions;
using Layeron.Scripting.Abstractions;

namespace Layeron.Plugins.PythonRunner;

/// <summary>
/// The Python runner: deploys an embedded CPython, keeps one interpreter session alive for the whole
/// application, and offers an interactive console and a file editor on top of it.
/// <para>
/// There is no plugin-source half, unlike the C# script host. A Python file is a working document - edited
/// and run against the current data - and is never installed as a plugin. That is a boundary rather than a
/// missing feature: Python code here has no contribution model to install into, so "install this script"
/// would have nothing to mean.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime.</b> The plugin lives for the application's lifetime. It has to: the interpreter is a
/// process-wide resource that pythonnet initialises once and cannot unload, so a plugin that came and went
/// would leave a runtime behind that nothing can talk to.
/// </para>
/// <para>
/// <b>Nothing is deployed at activation.</b> Deployment waits on the network and on child processes and can
/// take a minute on a first run, and the plugin host activates plugins on the UI thread - so the work begins
/// when the console is opened or the first script is run, and never before.
/// </para>
/// </remarks>
[Plugin("layeron.pythonrunner")]
public sealed class PythonRunnerPlugin : IPlugin
{
    private static readonly string[] MenuPath = ["Python"];

    public void Activate(IPluginContext context)
    {
        var host = context.Services.GetService<IScriptHostService>();
        var engine = new PythonSession(context.PluginDirectory ?? AppContext.BaseDirectory, context.Services)
        {
            // The data API's write calls land on the UI thread: the session's volume and mask manager are
            // UI-thread objects, and a script runs on a thread pool thread.
            UiInvoke = Dispatcher.UIThread.Invoke
        };

        context.Registry.Track(engine);

        var documents = new PythonDocuments(context, host, engine);
        context.Registry.Track(documents);

        // The workspace's Python folder is where the editor reads files from and the folder the engine puts
        // first on sys.path, so a script can import a helper sitting next to it. Re-registered whenever the
        // workspace changes, so a module the user keeps beside their scripts stays importable after a switch.
        documents.RegisterExisting();

        // An example written on the first run has to appear in the file list too, and the scan above ran
        // before the engine started. Scanning again registers only what is new - a file already listed keeps
        // its existing tab - and does not steal focus.
        engine.SampleDeployed += _ => documents.RegisterExisting();

        if (host is not null)
        {
            context.Registry.Track(new WorkspaceSubscription(host, () =>
            {
                engine.ModulePath = documents.WorkspaceDirectory;
                documents.RegisterExisting();
            }));
        }

        engine.ModulePath = documents.WorkspaceDirectory;

        // The console is a tool panel, not a document: it is a side panel the user keeps open while working,
        // and a tab that can never be saved and whose close looks like discarding data would be the wrong
        // shape for it. The handle comes back so the menu can reveal it rather than open a second copy.
        var repl = context.Registry.AddTool(new ToolContribution
        {
            Id = PythonPaths.ReplPanelId,
            Title = "Python",
            PreferredDock = DockHint.Bottom,
            ContentFactory = () => new PythonReplView(engine, context.Services.Log)
        });
        context.Registry.Track(repl);

        Menu(context, "REPL", repl.Open);
        Menu(context, "新建 Python 文件", documents.OpenNew);
        Menu(context, "打开 Python 文件…", () => _ = documents.OpenFileAsync());
        Menu(context, "重置会话", () => _ = engine.ResetSessionAsync());

        // The environment page lives in the settings window rather than in this menu, the way the host's own
        // configuration does. It is the one place that installs packages - nothing else calls pip, because
        // that is the only operation here that needs the network and can fail on a machine whose scripts
        // otherwise run fine.
        context.Registry.Track(context.Registry.AddSettingsPage(new SettingsPageContribution
        {
            Id = "layeron.pythonrunner.environment",
            Title = "Python",
            ContentFactory = () => new PythonSettingsView(engine, context.Services.Log)
        }));

        context.Registry.Contribute<IPythonRunnerService>("layeron.pythonrunner.python_runner",new PythonRunnerService(engine));
    }

    private static void Menu(IPluginContext context, string header, Action execute) =>
        context.Registry.AddMenu(new MenuContribution
        {
            Path = MenuPath,
            Header = header,
            Execute = execute
        });

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
