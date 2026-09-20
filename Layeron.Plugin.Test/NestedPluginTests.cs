using Layeron.Plugin.Abstractions;
using Layeron.Plugin.Host;

namespace Layeron.Plugin.Test;

/// <summary>
/// Covers the property that makes this design worth having: a plugin can be a plugin loader, and the
/// lifetime nesting is automatic - children are created inside the loader's scope, so unloading the
/// loader unloads them with no extra bookkeeping.
/// </summary>
[TestClass]
public sealed class NestedPluginTests
{
    [TestMethod]
    public void PluginSource_LoadsChildAsChildScope()
    {
        var adapter = new TestHostAdapter(TestVolumes.Small());
        using var manager = new PluginManager(adapter);

        manager.LoadInProcess(new LoaderPlugin(), "test.loader");

        Assert.IsTrue(LoaderPlugin.ChildActivated.Wait(TimeSpan.FromSeconds(5)),
            "the child plugin offered by the loader's source should have been activated");
        Assert.IsNotNull(manager.Plugins.SingleOrDefault(p => p.Id == LoaderPlugin.ChildId));
        Assert.IsTrue(adapter.Menus.ContainsKey($"{LoaderPlugin.ChildId}::From child"));
    }

    [TestMethod]
    public void UnloadingLoader_UnloadsItsPlugins()
    {
        var adapter = new TestHostAdapter(TestVolumes.Small());
        using var manager = new PluginManager(adapter);

        manager.LoadInProcess(new LoaderPlugin(), "test.loader");
        Assert.IsTrue(LoaderPlugin.ChildActivated.Wait(TimeSpan.FromSeconds(5)));

        Assert.IsTrue(manager.Unload("test.loader"));

        Assert.AreEqual(0, manager.Plugins.Count,
            "unloading a plugin loader must unload everything it loaded");
        Assert.AreEqual(0, adapter.Menus.Count, "the child's menu must be gone with its loader");
    }

    [TestMethod]
    public void ChildIsReportedAsOwnedByLoader()
    {
        var adapter = new TestHostAdapter(TestVolumes.Small());
        using var manager = new PluginManager(adapter);

        manager.LoadInProcess(new LoaderPlugin(), "test.loader");
        Assert.IsTrue(LoaderPlugin.ChildActivated.Wait(TimeSpan.FromSeconds(5)));

        Assert.AreEqual("test.loader", manager.GetOwnerPluginId(LoaderPlugin.ChildId));
        Assert.IsNull(manager.GetOwnerPluginId("test.loader"));
    }

    [TestMethod]
    public void UnloadingChild_LeavesLoaderRunning()
    {
        var adapter = new TestHostAdapter(TestVolumes.Small());
        using var manager = new PluginManager(adapter);

        manager.LoadInProcess(new LoaderPlugin(), "test.loader");
        Assert.IsTrue(LoaderPlugin.ChildActivated.Wait(TimeSpan.FromSeconds(5)));

        Assert.IsTrue(manager.Unload(LoaderPlugin.ChildId));

        Assert.AreEqual(1, manager.Plugins.Count);
        Assert.AreEqual("test.loader", manager.Plugins[0].Id);
        Assert.AreEqual(0, adapter.Menus.Count);
    }

    [TestMethod]
    public void ReloadSource_RediscoverAndReactivates()
    {
        var adapter = new TestHostAdapter(TestVolumes.Small());
        using var manager = new PluginManager(adapter);

        manager.LoadInProcess(new LoaderPlugin(), "test.loader");
        Assert.IsTrue(LoaderPlugin.ChildActivated.Wait(TimeSpan.FromSeconds(5)));
        LoaderPlugin.ChildActivated.Reset();

        var reloaded = manager.ReloadSource(LoaderPlugin.SourceId);

        Assert.IsTrue(reloaded.Contains(LoaderPlugin.ChildId),
            "reloading a source must bring its plugins back");
        Assert.IsTrue(LoaderPlugin.ChildActivated.Wait(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(1, manager.Plugins.Count(p => p.Id == LoaderPlugin.ChildId),
            "a reload must not leave a duplicate of the same plugin");
    }
}
