using Layeron.Plugin.Abstractions;
using Layeron.Plugin.Host;
using Layeron.Scripting.Abstractions;

namespace Layeron.Plugin.Test;

/// <summary>
/// End-to-end test of a plugin that is itself a plugin loader: the script host registers a
/// <see cref="IPluginSource"/>, the ordinary manager activates the scripts it offers as children of the
/// script host, and the scripts can even extend a compiled plugin's extension point. No host code path
/// knows scripts exist.
/// <para>
/// Roslyn compiles a script into an assembly it loads into the default context, while the script host
/// itself runs in its own collectible context. The globals type the script binds to therefore has to be
/// the one shared assembly both sides see - <see cref="ScriptGlobals"/> in
/// <c>Layeron.Plugin.Abstractions</c> - otherwise the two contexts would each hold a copy and the cast
/// Roslyn performs on the globals argument would fail.
/// </para>
/// </summary>
[TestClass]
// Script activation compiles C# with Roslyn inside a fresh collectible plugin context: each test costs
// several seconds just to warm the compiler. They are opt-in rather than part of the normal suite;
// see Layeron.Plugin.Test.runsettings.
[TestCategory("ScriptCompilation")]
public sealed class ScriptHostTests
{
    private const string ScriptHostId = "layeron.scripthost";
    private const string SourceId = "layeron.scripthost.csharp";

    [TestMethod]
    public void ScriptFile_IsLoadedAsAPlugin()
    {
        var directory = TestPaths.CreateTemporaryPluginDirectory();
        try
        {
            WriteScript(directory, "hello", """
                Menu("Hello from script", () => Log("script menu clicked"));
                """);

            var adapter = new TestHostAdapter(TestVolumes.Small());
            using var manager = CreateManager(adapter, directory);

            Assert.AreEqual(ScriptHostId, manager.LoadAssembly(TestPaths.ScriptHostPluginAssembly));

            Assert.IsTrue(manager.Plugins.Any(p => p.Id == "script:hello"),
                "the script should have been activated as a plugin");
            Assert.IsTrue(adapter.Menus.Keys.Any(k => k.EndsWith("::Hello from script", StringComparison.Ordinal)),
                "the script's menu entry should be attached");
            Assert.AreEqual(ScriptHostId, manager.GetOwnerPluginId("script:hello"),
                "the script must be owned by the script host that provided it");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void UnloadingScriptHost_UnloadsScripts()
    {
        var directory = TestPaths.CreateTemporaryPluginDirectory();
        try
        {
            WriteScript(directory, "hello", """
                Menu("Hello from script", () => { });
                """);

            var adapter = new TestHostAdapter(TestVolumes.Small());
            using var manager = CreateManager(adapter, directory);
            manager.LoadAssembly(TestPaths.ScriptHostPluginAssembly);
            Assert.IsTrue(adapter.Menus.Count > 0);

            Assert.IsTrue(manager.Unload(ScriptHostId));

            Assert.AreEqual(0, manager.Plugins.Count,
                "unloading the script host must unload the scripts it loaded");
            Assert.AreEqual(0, adapter.Menus.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void ReloadSource_PicksUpEditedScript()
    {
        var directory = TestPaths.CreateTemporaryPluginDirectory();
        try
        {
            WriteScript(directory, "editable", """
                Menu("First version", () => { });
                """);

            var adapter = new TestHostAdapter(TestVolumes.Small());
            using var manager = CreateManager(adapter, directory);
            manager.LoadAssembly(TestPaths.ScriptHostPluginAssembly);
            Assert.IsTrue(adapter.Menus.Keys.Any(k => k.EndsWith("::First version", StringComparison.Ordinal)));

            WriteScript(directory, "editable", """
                Menu("Second version", () => { });
                """);

            manager.ReloadSource(SourceId);

            Assert.IsTrue(adapter.Menus.Keys.Any(k => k.EndsWith("::Second version", StringComparison.Ordinal)),
                "a reload must pick up the edited script");
            Assert.IsFalse(adapter.Menus.Keys.Any(k => k.EndsWith("::First version", StringComparison.Ordinal)),
                "the previous version's contributions must be gone");
            Assert.AreEqual(1, manager.Plugins.Count(p => p.Id == "script:editable"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void Script_ExtendsACompiledPluginsExtensionPoint()
    {
        var directory = TestPaths.CreateTemporaryPluginDirectory();
        try
        {
            WriteScript(directory, "extender", """
                Contribute("layeron.threshold.actions", new VolumeAction
                {
                    Header = "From script",
                    Execute = services => services.Log.Info("script action ran")
                });
                """);

            var adapter = new TestHostAdapter(TestVolumes.Small());
            using var manager = CreateManager(adapter, directory);

            manager.LoadAssembly(TestPaths.ThresholdPluginAssembly);
            manager.LoadAssembly(TestPaths.ScriptHostPluginAssembly);

            var point = manager.PointRegistry.GetPoint<VolumeAction>("layeron.threshold.actions");
            Assert.IsNotNull(point);
            Assert.AreEqual(1, point.Count, "the script's action should be on the threshold plugin's point");

            // The script's action must be callable: this is the cross-plugin call path end to end.
            var executed = false;
            var scripted = new VolumeAction { Header = "From script", Execute = _ => executed = true };
            scripted.Execute(adapter.Services);
            Assert.IsTrue(executed);

            manager.Unload("script:extender");
            Assert.AreEqual(0, point.Count, "unloading the script must remove its contribution");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void BrokenScript_IsReportedAndDoesNotStopTheHost()
    {
        var directory = TestPaths.CreateTemporaryPluginDirectory();
        try
        {
            WriteScript(directory, "broken", "this is not valid C# at all (");
            WriteScript(directory, "good", """
                Menu("Good script", () => { });
                """);

            var adapter = new TestHostAdapter(TestVolumes.Small());
            using var manager = CreateManager(adapter, directory);

            Assert.AreEqual(ScriptHostId, manager.LoadAssembly(TestPaths.ScriptHostPluginAssembly),
                "a broken script must not fail the script host itself");

            Assert.IsTrue(manager.Plugins.Any(p => p.Id == "script:good"),
                "the remaining scripts must still load");
            Assert.IsTrue(adapter.Log.Contains("script:broken"),
                "the failure must be reported against the broken script");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void ScriptEditor_RegistersADocumentPerScript_AndItsOwnMenuOpensOne()
    {
        var directory = TestPaths.CreateTemporaryPluginDirectory();
        try
        {
            WriteScript(directory, "hello", """
                Menu("Hello from script", () => { });
                """);

            var adapter = new TestHostAdapter(TestVolumes.Small());
            ((TestServices)adapter.Services).AdditionalService =
                new FakeScriptHostService(directory);

            using var manager = CreateManager(adapter, directory);
            Assert.AreEqual(ScriptHostId, manager.LoadAssembly(TestPaths.ScriptHostPluginAssembly));
            // The editor is the plugin's own UI: it registers a document for every script it can see, so
            // a saved layout can restore a script tab.
            Assert.IsTrue(
                adapter.Documents.Keys.Any(k => k.EndsWith("::script:hello", StringComparison.Ordinal)),
                "the script host should register an editor document for each script it finds");

            // And its own menu must be able to bring one up - that is the Open() capability on a
            // contribution handle, without which a plugin could never show its own document.
            var newScript = adapter.Menus.Values.Single(m => m.Header == "新建脚本");
            newScript.Execute();

            Assert.IsTrue(
                adapter.Opened.Any(key => key.Contains("script:untitled", StringComparison.Ordinal)),
                "a plugin's own menu entry must be able to open a document it contributed");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void Script_ComputesOnTheCurrentVolume_AndItsResultsOutliveTheRun()
    {
        var directory = TestPaths.CreateTemporaryPluginDirectory();
        try
        {
            // A computation script: read the data, report, and hand a mask back. This is the shape a script
            // has when it is not an extension but a piece of numeric work on the loaded volume.
            WriteScript(directory, "compute", """
                if (Volume is ReadonlyBig3DArray<byte> v)
                {
                    Result("dims", v.X + "x" + v.Y + "x" + v.Z);

                    var roi = ReadRoi(0, 0, 0, 2, 2, 2);
                    Result("roi", roi is null ? "null" : "ok");

                    AddMask("script mask", new Big3DBitMap(v.X, v.Y, v.Z));
                }
                """);

            var adapter = new TestHostAdapter(TestVolumes.Small());
            ((TestServices)adapter.Services).AdditionalService =
                new FakeScriptHostService(directory);

            using var manager = CreateManager(adapter, directory);
            Assert.AreEqual(ScriptHostId, manager.LoadAssembly(TestPaths.ScriptHostPluginAssembly),
                "load failed; log: " + string.Join(" | ", adapter.Log.Messages));

            // Reading the volume through the script API works, and Result() reaches the log with its value.
            // A script logs through IPluginServices, which is a different sink from the host adapter's log.
            var scriptLog = (CollectingLog)((TestServices)adapter.Services).Log;
            Assert.IsTrue(scriptLog.Contains("40x40x40"),
                "the script's Result must carry the value; log was: " + string.Join(" | ", scriptLog.Messages));
            Assert.IsTrue(scriptLog.Contains("ok"),
                "ReadRoi must hand the script an NDArray; log was: " + string.Join(" | ", scriptLog.Messages));

            // A mask a script writes is session data, exactly like one a compiled plugin writes.
            Assert.IsTrue(
                ((TestMaskService)adapter.Services.Masks).Layers.Any(l => l.Name == "script mask"),
                "AddMask must register a layer on the session");

            // Nothing replaced the volume, so the staleness warning must stay silent.
            Assert.IsFalse(scriptLog.Contains("已被替换"),
                "an unchanged volume must not be reported as stale; log was: "
                    + string.Join(" | ", scriptLog.Messages));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void Script_MaskFromAReplacedVolume_IsWarnedAboutButStillAdded()
    {
        var directory = TestPaths.CreateTemporaryPluginDirectory();
        try
        {
            // The volume moves on while the script holds it: the same situation as a load landing mid-run.
            // The mask must still reach the session. The baseline is as old as the globals object, so it
            // cannot tell a stale result apart from a long-lived script that re-read the current volume -
            // blocking on it would reject good data, which is why it only warns.
            WriteScript(directory, "stale", """
                if (Volume is ReadonlyBig3DArray<byte> v)
                {
                    Host.Volumes.Replace(v);
                    AddMask("after replace", new Big3DBitMap(v.X, v.Y, v.Z));
                }
                """);

            var adapter = new TestHostAdapter(TestVolumes.Small());
            ((TestServices)adapter.Services).AdditionalService =
                new FakeScriptHostService(directory);

            using var manager = CreateManager(adapter, directory);
            Assert.AreEqual(ScriptHostId, manager.LoadAssembly(TestPaths.ScriptHostPluginAssembly),
                "load failed; log: " + string.Join(" | ", adapter.Log.Messages));

            var scriptLog = (CollectingLog)((TestServices)adapter.Services).Log;

            Assert.IsTrue(
                ((TestMaskService)adapter.Services.Masks).Layers.Any(l => l.Name == "after replace"),
                "a mask from a replaced volume must still be added; log was: "
                    + string.Join(" | ", scriptLog.Messages));

            Assert.IsTrue(scriptLog.Contains("已被替换"),
                "the replaced volume must be reported as a warning; log was: "
                    + string.Join(" | ", scriptLog.Messages));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void WorkspaceScript_IsNotActivatedAsAPlugin_ButIsOpenableInTheEditor()
    {
        var pluginDirectory = TestPaths.CreateTemporaryPluginDirectory();
        var workspace = TestPaths.CreateTemporaryPluginDirectory();
        try
        {
            // A script in the user's workspace: a working document. Nothing was installed.
            var workspaceScripts = Path.Combine(workspace, "scripts");
            Directory.CreateDirectory(workspaceScripts);
            File.WriteAllText(Path.Combine(workspaceScripts, "mine.csx"),
                """Menu("from workspace", () => { });""");

            var adapter = new TestHostAdapter(TestVolumes.Small());
            ((TestServices)adapter.Services).AdditionalService = new FakeScriptHostService(workspace);

            using var manager = CreateManager(adapter, pluginDirectory);
            Assert.AreEqual(ScriptHostId, manager.LoadAssembly(TestPaths.ScriptHostPluginAssembly));

            // Saving a script into the workspace must not install it: activating it would run code the user
            // only wrote, and its menu would appear in the app's menu bar.
            Assert.IsFalse(manager.Plugins.Any(p => p.Id == "script:mine"),
                "a workspace script must not be activated as a plugin");
            Assert.IsFalse(adapter.Menus.Values.Any(m => m.Header == "from workspace"),
                "a workspace script must not contribute to the app menu");

            // It is still one click away: the editor offers the workspace's scripts as documents.
            Assert.IsTrue(adapter.Documents.Keys.Any(k => k.EndsWith("::script:mine", StringComparison.Ordinal)),
                "the editor should list the workspace's scripts");
        }
        finally
        {
            Directory.Delete(pluginDirectory, recursive: true);
            Directory.Delete(workspace, recursive: true);
        }
    }

    private static PluginManager CreateManager(TestHostAdapter adapter, string pluginDirectory) =>
        new(adapter, new PluginManagerOptions
        {
            PluginDirectory = pluginDirectory,
            DevelopmentPluginDirectory = Path.Combine(pluginDirectory, "dev")
        });

    private static void WriteScript(string pluginDirectory, string name, string code)
    {
        var scriptDirectory = Path.Combine(pluginDirectory, "scripts");
        Directory.CreateDirectory(scriptDirectory);
        File.WriteAllText(Path.Combine(scriptDirectory, $"{name}.csx"), code);
    }

    /// <summary>Host capabilities the editor reaches for; only the workspace root matters here.</summary>
    private sealed class FakeScriptHostService(string workspaceRoot) : IScriptHostService
    {
        public event Action? WorkspaceChanged;

        public string WorkspaceRoot { get; } = workspaceRoot;

        public Task<string?> PickFileToOpenAsync(string title, string suggestedDirectory, IReadOnlyList<string> patterns) =>
            Task.FromResult<string?>(null);

        public Task<string?> PickFileToSaveAsync(string title, string suggestedDirectory, string suggestedFileName, string defaultExtension, IReadOnlyList<string> patterns) =>
            Task.FromResult<string?>(null);

        public void ReloadAllScripts() { }

        public void LogRunFailure(string name, string? error) => _failures.Add($"{name}: {error}");

        private readonly List<string> _failures = [];

        /// <summary>Test hook: mimics the host switching workspace.</summary>
        public void RaiseWorkspaceChanged() => WorkspaceChanged?.Invoke();
    }
}
