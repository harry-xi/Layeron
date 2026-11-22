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

        // A tool panel only. The plugin used to contribute a menu entry as well, and it was removed on
        // purpose ("feat: improve some ui"); a menu here would mean something put one back.
        Assert.IsFalse(adapter.Menus.Keys.Any(k => k.StartsWith("layeron.threshold::")),
            "the threshold plugin contributes a tool panel but no menu");
    }

    [TestMethod]
    public void ThresholdPlugin_UnloadRemovesContributionsAndCollectsContext()
    {
        var adapter = new TestHostAdapter(TestVolumes.Small());
        var manager = new PluginManager(adapter);

        var toolAttached = LoadInspectAndUnload(manager, adapter);

        Assert.IsTrue(toolAttached, "the plugin's tool must be attached while it is loaded");
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
    /// The 3D plugin contributes a management panel and a document, sharing one model manager. Both
    /// content factories would create UI and (for the document) start a GPU engine, so this is also
    /// the proof that a plugin can be loaded and unloaded without either content ever being created:
    /// the contributions hand the host factories, and nothing here invokes them.
    /// </summary>
    [TestMethod]
    public void Model3DPlugin_LoadsAndContributesToolAndDocument()
    {
        var adapter = new TestHostAdapter(TestVolumes.Small());
        var manager = new PluginManager(adapter);

        var (id, name) = LoadInspectAndUnloadModel3D(manager, adapter);

        Assert.AreEqual("layeron.model3d", id, "the id must come from the assembly metadata");
        Assert.AreEqual("Mask 3D Model", name);
        Assert.AreEqual(0, adapter.Tools.Count, "tools must be detached on unload");
        Assert.AreEqual(0, adapter.Documents.Count, "documents must be detached on unload");
        Assert.AreEqual(0, manager.Plugins.Count);

        var leaked = manager.VerifyUnloaded();
        Assert.AreEqual(0, leaked.Count, $"leaked load contexts: {string.Join(", ", leaked)}");
        manager.Dispose();
    }

    /// <summary>
    /// The morphology plugin contributes one lazy tool panel and one slice tool. Activation must not
    /// create the panel or touch a mask, and unloading must leave no reachable reference to the
    /// plugin's load context.
    /// </summary>
    [TestMethod]
    public void MaskMorphologyPlugin_LoadsContributesToolAndCollectsContext()
    {
        var adapter = new TestHostAdapter(TestVolumes.Small());
        var manager = new PluginManager(adapter);

        var (id, name) = LoadInspectAndUnloadMorphology(manager, adapter);

        Assert.AreEqual("layeron.maskmorphology", id, "the id must come from the assembly metadata");
        Assert.AreEqual("Mask Morphology", name);
        Assert.AreEqual(0, adapter.Tools.Count, "tools must be detached on unload");
        Assert.AreEqual(0, manager.Plugins.Count);

        var leaked = manager.VerifyUnloaded();
        Assert.AreEqual(0, leaked.Count, $"leaked load contexts: {string.Join(", ", leaked)}");
        manager.Dispose();
    }

    private static (string Id, string Name) LoadInspectAndUnloadMorphology(
        PluginManager manager, TestHostAdapter adapter)
    {
        manager.LoadAssembly(TestPaths.MaskMorphologyPluginAssembly);

        var plugin = manager.Plugins.Single();
        Assert.IsTrue(adapter.Tools.ContainsKey("layeron.maskmorphology::morphology"),
            "the morphology panel must be attached to the host");
        Assert.AreEqual(1, adapter.Tools.Count);
        Assert.AreEqual(0, adapter.Opened.Count, "loading the plugin must not create the panel");

        var sliceTools = manager.PointRegistry.GetPoint<SliceToolContribution>(ToolPoints.SliceTools);
        Assert.IsNotNull(sliceTools, "the host-owned slice tool point must exist");
        Assert.AreEqual(1, sliceTools.Count, "region growing must be contributed as a slice tool");
        Assert.AreEqual("layeron.maskmorphology.regiongrow", sliceTools.Items[0].Descriptor.Id);

        var result = (plugin.Descriptor.Id, plugin.Descriptor.Name ?? "");
        manager.Unload("layeron.maskmorphology");
        return result;
    }

    /// <summary>
    /// Same frame rule as <see cref="LoadInspectAndUnload"/>: the plugin instance must be out of scope
    /// before the leak check, so only value types may cross back into the caller's frame.
    /// </summary>
    private static (string Id, string Name) LoadInspectAndUnloadModel3D(
        PluginManager manager, TestHostAdapter adapter)
    {
        manager.LoadAssembly(TestPaths.Model3DPluginAssembly);

        var plugin = manager.Plugins.Single();
        Assert.IsTrue(adapter.Tools.ContainsKey("layeron.model3d::mask3d.models"),
            "the creation/management panel must be attached to the host");
        Assert.IsTrue(adapter.Documents.ContainsKey("layeron.model3d::mask3d"),
            "the plugin's document must be attached to the host");
        Assert.AreEqual(1, adapter.Tools.Count);
        Assert.AreEqual(1, adapter.Documents.Count);
        Assert.AreEqual(0, adapter.Opened.Count, "neither surface may be created just by loading the plugin");

        var result = (plugin.Descriptor.Id, plugin.Descriptor.Name ?? "");
        manager.Unload("layeron.model3d");
        return result;
    }

    /// <summary>
    /// Loads and unloads the plugin in its own frame. The frame must be gone before the leak check,
    /// because any live <c>LoadedPlugin</c> - even a temporary one - references the plugin instance and
    /// would pin the load context, making the check fail for the wrong reason.
    /// </summary>
    private static bool LoadInspectAndUnload(PluginManager manager, TestHostAdapter adapter)
    {
        manager.LoadAssembly(TestPaths.ThresholdPluginAssembly);

        var toolAttached = adapter.Tools.ContainsKey("layeron.threshold::threshold");

        manager.Unload("layeron.threshold");
        return toolAttached;
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
