using System.IO;
using Layeron.App.Models;
using Layeron.App.Plugins;
using Layeron.App.Session;
using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReactiveUI.Builder;

namespace Layeron.App.Test;

/// <summary>
/// Covers the seam between the mask manager and the plugin-facing mask service. A mask the user creates
/// or loads in the panel has to be visible to plugins and scripts, not only to the panel and the slice
/// views - otherwise a script cannot read a mask the user drew.
/// </summary>
[TestClass]
public sealed class MaskServiceTests
{
    [ClassInitialize]
    public static void InitializeReactiveUI(TestContext _) =>
        RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();

    public TestContext? TestContext { get; set; }

    private static (SessionManager Sessions, ISession Session, AppPluginServices Services) NewSession()
    {
        var sessions = new SessionManager();
        var session = sessions.CreateSession();
        session.SharedData.Array = new Big3DArray<byte>(8, 9, 10);
        var services = new AppPluginServices(sessions, new TestPluginLog());
        return (sessions, session, services);
    }

    [TestMethod]
    public void Layers_IncludesMasksAddedThroughTheManager()
    {
        var (_, session, services) = NewSession();
        using (services)
        {
            var changed = 0;
            services.Masks.Changed += () => changed++;

            // Both the panel's "add" and its "load" path end in AddLayer.
            var layer = new MaskLayer { Name = "panel", Bitmap = new Big3DBitMap(8, 9, 10) };
            session.MaskManager.AddLayer(layer);

            Assert.HasCount(1, services.Masks.Layers, "a panel layer must reach the plugin service");
            Assert.AreSame(layer, services.Masks.Layers[0]);
            Assert.AreEqual(1, changed, "listeners must be told about a layer they did not create");

            session.MaskManager.RemoveLayer(layer);

            Assert.IsEmpty(services.Masks.Layers);
            Assert.AreEqual(2, changed);
        }
    }

    [TestMethod]
    public void Layers_IncludesMasksReadFromDisk()
    {
        var (_, session, services) = NewSession();
        using (services)
        {
            // The exact objects the load command produces: a MaskLayer deserialised from a .b3m stream,
            // then handed to the manager.
            var saved = new MaskLayer
            {
                Name = "loaded",
                Bitmap = new Big3DBitMap(8, 9, 10)
            };
            using var stream = new MemoryStream();
            saved.SaveStream(stream);
            stream.Position = 0;

            var loaded = MaskLayer.LoadStream(stream, 8, 9, 10);
            Assert.IsNotNull(loaded);

            session.MaskManager.AddLayer(loaded);

            Assert.HasCount(1, services.Masks.Layers);
            Assert.AreEqual("loaded", services.Masks.Layers[0].Name);
            Assert.AreSame(loaded, session.MaskManager.ActiveMask);
        }
    }

    [TestMethod]
    public void ContentChanged_FromTheManager_ReachesThePluginService()
    {
        var (_, session, services) = NewSession();
        using (services)
        {
            var contentChanged = 0;
            services.Masks.ContentChanged += () => contentChanged++;

            session.MaskManager.RaiseMasksContentChanged();

            Assert.AreEqual(1, contentChanged, "a pixel change made in the panel must wake plugin listeners");
        }
    }

    [TestMethod]
    public void Layers_FollowTheActiveSession()
    {
        var (sessions, session, services) = NewSession();
        using (services)
        {
            session.MaskManager.AddLayer(new MaskLayer { Name = "old", Bitmap = new Big3DBitMap(8, 9, 10) });
            Assert.HasCount(1, services.Masks.Layers);

            var next = sessions.CreateSession();
            next.SharedData.Array = new Big3DArray<byte>(8, 9, 10);

            Assert.IsEmpty(services.Masks.Layers, "layers belong to the session that created them");

            var changed = 0;
            services.Masks.Changed += () => changed++;

            next.MaskManager.AddLayer(new MaskLayer { Name = "new", Bitmap = new Big3DBitMap(8, 9, 10) });

            Assert.HasCount(1, services.Masks.Layers);
            Assert.AreEqual("new", services.Masks.Layers[0].Name);
            Assert.AreEqual(1, changed, "the new session's layers are reported");
        }
    }
}
