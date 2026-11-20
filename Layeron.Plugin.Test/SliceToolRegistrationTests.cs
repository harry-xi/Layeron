using Layeron.Plugin.Abstractions;
using Layeron.Plugin.Host;

namespace Layeron.Plugin.Test;

/// <summary>
/// Slice tool registration: <c>AddSliceTool</c> lands on the host-owned
/// <see cref="ToolPoints.SliceTools"/> point, the contribution carries the tool's descriptor and
/// factory, a duplicate id fails the activation without leaving anything behind, and the point
/// survives the contributor's unload while the contribution does not. Tool execution itself
/// (routing, masks, undo) lives in the host app, not here.
/// </summary>
[TestClass]
public sealed class SliceToolRegistrationTests
{
    [TestMethod]
    public void AddSliceTool_LandsOnHostOwnedPoint()
    {
        var adapter = new TestHostAdapter(TestVolumes.Small());
        using var manager = new PluginManager(adapter);

        Assert.IsNotNull(manager.LoadInProcess(new SliceToolPlugin(), "test.slicetool"));

        var point = manager.PointRegistry.GetPoint<SliceToolContribution>(ToolPoints.SliceTools);
        Assert.IsNotNull(point, "AddSliceTool must define the host-owned slice tool point");
        Assert.IsNull(point.OwnerPluginId, "the slice tool point is host-owned, not plugin-owned");
        Assert.AreEqual(1, point.Count);

        var contribution = point.Items[0];
        Assert.AreEqual(RecordingSliceTool.SharedDescriptor.Id, contribution.Descriptor.Id);
        Assert.AreEqual(RecordingSliceTool.SharedDescriptor.Group, contribution.Descriptor.Group,
            "the toolbox must be able to group a plugin tool without creating an instance");
        Assert.AreSame(SliceToolPlugin.Tool, contribution.Factory(),
            "the factory must produce the very tool the plugin registered");
    }

    [TestMethod]
    public void AddSliceTool_IsCallableWithoutAnyPlugin()
    {
        // The point is host-defined: it exists before any plugin contributes, which is what lets
        // the app-side ToolCatalog attach at startup and follow loads afterwards.
        var adapter = new TestHostAdapter(TestVolumes.Small());
        using var manager = new PluginManager(adapter);

        var point = manager.PointRegistry.GetPoint<SliceToolContribution>(ToolPoints.SliceTools);
        Assert.IsNotNull(point);
        Assert.AreEqual(0, point.Count);
    }

    [TestMethod]
    public void DuplicateSliceToolId_IsRejected()
    {
        var adapter = new TestHostAdapter(TestVolumes.Small());
        using var manager = new PluginManager(adapter);

        Assert.IsNotNull(manager.LoadInProcess(new SliceToolPlugin(), "test.slicetool"));
        Assert.IsNull(manager.LoadInProcess(new SliceToolPlugin(), "test.slicetool.dup"),
            "a duplicate slice tool id must fail activation");

        var point = manager.PointRegistry.GetPoint<SliceToolContribution>(ToolPoints.SliceTools);
        Assert.IsNotNull(point);
        Assert.AreEqual(1, point.Count, "the failed registration must leave nothing behind");
        Assert.IsTrue(adapter.Log.Contains(RecordingSliceTool.SharedDescriptor.Id),
            "the rejected tool id must be visible in the log");
    }

    [TestMethod]
    public void Unload_RemovesContributionButKeepsPoint()
    {
        var adapter = new TestHostAdapter(TestVolumes.Small());
        using var manager = new PluginManager(adapter);

        manager.LoadInProcess(new SliceToolPlugin(), "test.slicetool");
        var point = manager.PointRegistry.GetPoint<SliceToolContribution>(ToolPoints.SliceTools);
        Assert.IsNotNull(point);
        Assert.AreEqual(1, point.Count);

        Assert.IsTrue(manager.Unload("test.slicetool"));

        Assert.AreEqual(0, point.Count, "the slice tool contribution must be removed on unload");
        Assert.IsNotNull(manager.PointRegistry.GetPoint<SliceToolContribution>(ToolPoints.SliceTools),
            "the host-owned point itself must survive the contributor's unload");
    }
}
