using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;
using Layeron.Plugin.Host;

namespace Layeron.Plugin.Test;

[TestClass]
public sealed class PluginLifecycleTests
{
    [TestMethod]
    public void LoadInProcess_RegistersContributions()
    {
        var adapter = new TestHostAdapter(TestVolumes.Small());
        using var manager = new PluginManager(adapter);

        var id = manager.LoadInProcess(new PublisherPlugin(), "test.publisher", "Publisher");

        Assert.AreEqual("test.publisher", id);
        Assert.AreEqual(1, manager.Plugins.Count);
        Assert.IsTrue(adapter.Menus.ContainsKey("test.publisher::From publisher"));
    }

    [TestMethod]
    public void Unload_RemovesEveryContribution()
    {
        var adapter = new TestHostAdapter(TestVolumes.Small());
        using var manager = new PluginManager(adapter);

        manager.LoadInProcess(new PublisherPlugin(), "test.publisher");
        manager.LoadInProcess(new ConsumerPlugin(), "test.consumer");

        Assert.AreEqual(1, adapter.Tools.Count, "consumer tool should be attached");
        Assert.AreEqual(1, adapter.Documents.Count, "consumer document should be attached");
        var point = manager.PointRegistry.GetPoint<VolumeAction>(TestPoints.Actions);
        Assert.IsNotNull(point);
        Assert.AreEqual(1, point.Count, "consumer action should be on the point");

        Assert.IsTrue(manager.Unload("test.consumer"));

        Assert.AreEqual(0, adapter.Tools.Count, "tools must be detached on unload");
        Assert.AreEqual(0, adapter.Documents.Count, "documents must be detached on unload");
        Assert.IsEmpty(adapter.Menus.Where(k => k.Key.StartsWith("test.consumer")), "menus must be detached");
        Assert.AreEqual(0, point.Count, "contributions to another plugin's point must be removed");
        Assert.IsTrue(manager.PointRegistry.GetPoint<VolumeAction>(TestPoints.Actions) is not null,
            "the point itself belongs to the publisher and must survive");
    }

    [TestMethod]
    public void Unload_RemovesPreviewsPublishedByThePlugin()
    {
        var adapter = new TestHostAdapter(TestVolumes.Small());
        using var manager = new PluginManager(adapter);

        manager.LoadInProcess(new ProducerPlugin(), "test.producer");
        Assert.AreEqual(1, adapter.Services.Preview.Overlays.Count);
        Assert.AreEqual(1, adapter.Services.Masks.Layers.Count);

        manager.Unload("test.producer");

        Assert.AreEqual(0, adapter.Services.Preview.Overlays.Count, "a plugin must not leave previews behind");
        Assert.AreEqual(1, adapter.Services.Masks.Layers.Count,
            "masks are user data: they outlive the plugin that produced them");
    }

    [TestMethod]
    public void DefiningPluginUnload_RemovesItsPoint()
    {
        var adapter = new TestHostAdapter(TestVolumes.Small());
        using var manager = new PluginManager(adapter);

        manager.LoadInProcess(new PublisherPlugin(), "test.publisher");
        Assert.IsNotNull(manager.PointRegistry.GetPoint<VolumeAction>(TestPoints.Actions));

        manager.Unload("test.publisher");

        Assert.IsNull(manager.PointRegistry.GetPoint<VolumeAction>(TestPoints.Actions),
            "a point defined by a plugin must disappear with that plugin");
    }

    [TestMethod]
    public void ActivateThrows_LeavesNothingBehind()
    {
        var adapter = new TestHostAdapter(TestVolumes.Small());
        using var manager = new PluginManager(adapter);

        var id = manager.LoadInProcess(new ThrowingPlugin(), "test.throwing");

        Assert.IsNull(id, "a plugin that throws during activation must not be reported as loaded");
        Assert.AreEqual(0, manager.Plugins.Count);
        Assert.AreEqual(0, adapter.Menus.Count,
            "contributions made before the failure must be rolled back");
        Assert.IsTrue(adapter.Log.Contains("test.throwing"));
    }

    [TestMethod]
    public void DuplicateId_IsRejected()
    {
        var adapter = new TestHostAdapter(TestVolumes.Small());
        using var manager = new PluginManager(adapter);

        Assert.IsNotNull(manager.LoadInProcess(new PublisherPlugin(), "test.publisher"));
        Assert.IsNull(manager.LoadInProcess(new PublisherPlugin(), "test.publisher"));
        Assert.AreEqual(1, manager.Plugins.Count);
    }

    [TestMethod]
    public void ManagerDispose_UnloadsEverything()
    {
        var adapter = new TestHostAdapter(TestVolumes.Small());
        var manager = new PluginManager(adapter);

        manager.LoadInProcess(new PublisherPlugin(), "test.publisher");
        manager.LoadInProcess(new ConsumerPlugin(), "test.consumer");
        manager.Dispose();

        Assert.AreEqual(0, manager.Plugins.Count);
        Assert.AreEqual(0, adapter.Tools.Count);
        Assert.AreEqual(0, adapter.Menus.Count);
    }

    [Plugin("test.throwing")]
    private sealed class ThrowingPlugin : IPlugin
    {
        public void Activate(IPluginContext context)
        {
            context.Registry.AddMenu(new MenuContribution
            {
                Path = "Test/Throwing",
                Header = "Registered then failed",
                Execute = () => { }
            });

            throw new InvalidOperationException("activation failed on purpose");
        }
    }
}
