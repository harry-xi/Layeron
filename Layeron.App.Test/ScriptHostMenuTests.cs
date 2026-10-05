using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Layeron.App.Plugins;
using Layeron.App.Session;
using Layeron.App.ViewModels;
using Layeron.App.Workspaces;
using Layeron.Plugin.Host;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReactiveUI.Builder;

namespace Layeron.App.Test;

/// <summary>
/// Verifies the application half of plugin menus: a real plugin, loaded through the real
/// <see cref="PluginController"/>, must produce the nested menu tree the window renders.
/// <para>
/// This exists because "the menu item is missing" is otherwise only observable by eye. The script editor
/// is contributed entirely from the plugin, so if either half of the path - the plugin's
/// <c>AddMenu</c> or the host's tree building - regresses, the symptom is an empty menu with no error.
/// </para>
/// </summary>
[TestClass]
public sealed class ScriptHostMenuTests
{
    [ClassInitialize]
    public static void InitializeReactiveUI(TestContext _) =>
        RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();

    public TestContext? TestContext { get; set; }

    [TestMethod]
    public void LoadedScriptHost_ContributesOnlyWindowCommandsToThePluginMenuTree()
    {
        using var temp = new TempDirectory();

        var sessions = new SessionManager();
        var services = new AppPluginServices(sessions, new TestPluginLog());
        var dock = new DockViewModel(sessions, services.Preview);

        // Plugin directories are redirected too: with the defaults the script source would scan the user's
        // real plugin folder and activate whatever scripts are installed there, running their code here.
        var controller = new PluginController(services, dock, new PluginManagerOptions
        {
            PluginDirectory = temp.Path("plugins"),
            DevelopmentPluginDirectory = temp.Path("plugins.dev")
        });

        // The host half of the scripting seam; the plugin reads it during activation. Both paths are
        // redirected so the test never touches the user's real workspace or its remembered setting.
        var workspaces = new WorkspaceManager(new TestPluginLog(), temp.Path("workspace.json"), temp.Path("Default"));
        services.ScriptHost = new AppScriptHostService(controller, workspaces);
        controller.EnsureDirectories();

        try
        {
            Assert.AreEqual("layeron.scripthost", controller.LoadAssembly(ScriptHostAssemblyPath),
                "the script host plugin must load; otherwise no script editor menu can exist");

            var dump = Dump(controller.MenuNodes);

            // The menu's job is to produce an editor window, so the entries that do that must be there.
            Assert.IsNotNull(Find(controller.MenuNodes, "脚本", "新建脚本"),
                "expected 插件 → 脚本 → 新建脚本 in the plugin menu; actual tree: " + dump);

            Assert.IsNotNull(Find(controller.MenuNodes, "脚本", "打开脚本…"),
                "expected 插件 → 脚本 → 打开脚本… in the plugin menu; actual tree: " + dump);

            // ...while everything that acts on one script belongs to that script's own window. A menu has
            // no subject, so these entries must not exist here.
            foreach (var windowCommand in new[] { "保存", "另存为…", "运行", "取消" })
            {
                Assert.IsNull(Find(controller.MenuNodes, "脚本", windowCommand),
                    $"'{windowCommand}' acts on a single script, so it belongs on the editor window, not in the app menu");
            }
        }
        finally
        {
            controller.Dispose();
        }
    }

    /// <summary>Walks a slash-path's worth of headers, or returns null when it is not there.</summary>
    private static PluginMenuNode? Find(IReadOnlyList<PluginMenuNode> nodes, params string[] path)
    {
        var level = nodes;
        PluginMenuNode? node = null;

        foreach (var segment in path)
        {
            node = level.FirstOrDefault(n => string.Equals(n.Header, segment, StringComparison.OrdinalIgnoreCase));
            if (node is null)
                return null;

            level = node.Children;
        }

        return node;
    }

    private static string Dump(IReadOnlyList<PluginMenuNode> nodes) =>
        string.Join(" | ", nodes.Select(Describe));

    private static string Describe(PluginMenuNode node) =>
        node.Children.Count == 0 ? node.Header : $"{node.Header}({string.Join(",", node.Children.Select(Describe))})";

    private static string Configuration =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    /// <summary>The built script host plugin: loaded exactly as the app loads it, from its own folder.</summary>
    private static string ScriptHostAssemblyPath { get; } = Path.Combine(
        FindRepositoryRoot(), "Layeron.Plugins.ScriptHost", "bin", Configuration, "net10.0",
        "Layeron.Plugins.ScriptHost.dll");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Layeron.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Could not locate the repository root from '{AppContext.BaseDirectory}'.");
    }
}
