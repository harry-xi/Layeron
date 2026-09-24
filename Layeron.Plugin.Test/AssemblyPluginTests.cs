using System.Runtime.CompilerServices;
using Layeron.Plugin.Abstractions;
using Layeron.Plugin.Host;

namespace Layeron.Plugin.Test;

/// <summary>
/// Loads the real sample plugin assemblies from disk, unloads them, and checks that their load
/// contexts are actually collected. This is the end-to-end proof that assembly-metadata discovery,
/// collectible loading, scoped contributions and unload all work together.
/// </summary>
[TestClass]
public sealed class AssemblyPluginTests
{
    [TestMethod]
    public void ThresholdPlugin_LoadsFromAssemblyMetadata()
    {
        var adapter = new TestHostAdapter(TestVolumes.Small());
        using var manager = new PluginManager(adapter);

        var id = manager.LoadAssembly(TestPaths.ThresholdPluginAssembly);

        Assert.AreEqual("layeron.threshold", id, "the id must come from the assembly metadata");
        var plugin = manager.Plugins.Single();
        Assert.AreEqual("Threshold Segmentation", plugin.Descriptor.Name);
        Assert.AreEqual("1.0.0", plugin.Descriptor.Version);
        Assert.IsFalse(plugin.IsInProcess);

        Assert.IsTrue(adapter.Tools.ContainsKey("layeron.threshold::threshold"),
            "the plugin's tool must be attached to the host");
        Assert.IsTrue(adapter.Menus.Keys.Any(k => k.StartsWith("layeron.threshold::")));
    }

    [TestMethod]
    public void ThresholdPlugin_UnloadRemovesContributionsAndCollectsContext()
    {
        var adapter = new TestHostAdapter(TestVolumes.Small());
        var manager = new PluginManager(adapter);

        var (toolAttached, menuCount) = LoadInspectAndUnload(manager, adapter);

        Assert.IsTrue(toolAttached, "the plugin's tool must be attached while it is loaded");
        Assert.IsTrue(menuCount > 0, "the plugin's menu entries must be attached while it is loaded");
        Assert.AreEqual(0, adapter.Tools.Count, "tools must be detached on unload");
        Assert.AreEqual(0, adapter.Menus.Count, "menus must be detached on unload");

        // A surviving load context means something still references plugin code: a static, a leaked
        // handler, a cached view model. Treat it as a failure so regressions are caught here.
        var leaked = manager.VerifyUnloaded();
        Assert.AreEqual(0, leaked.Count, $"leaked load contexts: {string.Join(", ", leaked)}");

        Assert.AreEqual(0, manager.Plugins.Count);
        manager.Dispose();
    }

    /// <summary>
    /// Loads and unloads the plugin in its own frame. The frame must be gone before the leak check,
    /// because any live <c>LoadedPlugin</c> - even a temporary one - references the plugin instance and
    /// would pin the load context, making the check fail for the wrong reason.
    /// </summary>
    private static (bool ToolAttached, int MenuCount) LoadInspectAndUnload(PluginManager manager, TestHostAdapter adapter)
    {
        manager.LoadAssembly(TestPaths.ThresholdPluginAssembly);

        var toolAttached = adapter.Tools.ContainsKey("layeron.threshold::threshold");
        var menuCount = adapter.Menus.Count;

        manager.Unload("layeron.threshold");
        return (toolAttached, menuCount);
    }

    [TestMethod]
    public void ThresholdPlugin_ReloadsAndProducesAFreshInstance()
    {
        var adapter = new TestHostAdapter(TestVolumes.Small());
        using var manager = new PluginManager(adapter);

        manager.LoadAssembly(TestPaths.ThresholdPluginAssembly);
        var first = manager.Plugins.Single().Plugin;

        Assert.IsTrue(manager.Reload("layeron.threshold"));

        Assert.AreEqual(1, manager.Plugins.Count);
        var second = manager.Plugins.Single().Plugin;
        Assert.AreNotSame(first, second, "a hot reload must create a new plugin instance");
        Assert.AreEqual("layeron.threshold", manager.Plugins.Single().Id);
        Assert.IsTrue(adapter.Tools.ContainsKey("layeron.threshold::threshold"));
    }

    [TestMethod]
    public void NonPluginAssembly_IsSkippedQuietly()
    {
        var adapter = new TestHostAdapter(TestVolumes.Small());
        using var manager = new PluginManager(adapter);

        // A contract assembly sits in the same folder but declares no plugin identity.
        var id = manager.LoadAssembly(typeof(IPlugin).Assembly.Location);

        Assert.IsNull(id);
        Assert.AreEqual(0, manager.Plugins.Count);
        Assert.IsFalse(adapter.Log.Contains("ERR"));
    }

    [TestMethod]
    public void ExtensionPoint_PublishedByPlugin_AcceptsExternalContributions()
    {
        var adapter = new TestHostAdapter(TestVolumes.Small());
        using var manager = new PluginManager(adapter);

        manager.LoadAssembly(TestPaths.ThresholdPluginAssembly);

        // The threshold plugin owns this point. Another plugin (or a script) can feed it without
        // referencing the threshold assembly, because the payload is a host-standard type.
        var point = manager.PointRegistry.GetPoint<VolumeAction>("layeron.threshold.actions");
        Assert.IsNotNull(point, "the threshold plugin must publish its extension point");

        var handle = point.Add(new VolumeAction { Header = "contributed", Execute = _ => { } }, "some.other.plugin");
        Assert.AreEqual(1, point.Count);

        handle.Dispose();
        Assert.AreEqual(0, point.Count, "removing a contribution must take effect immediately");

        manager.Unload("layeron.threshold");
        Assert.IsNull(manager.PointRegistry.GetPoint<VolumeAction>("layeron.threshold.actions"),
            "the point must disappear with the plugin that defined it");
    }
}
