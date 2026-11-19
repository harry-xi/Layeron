using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Dock.Avalonia.Controls;
using Dock.Avalonia.Themes.Simple;
using Dock.Model;
using Dock.Model.Controls;
using Dock.Model.Core;
using Layeron.App;
using Layeron.App.Layout;
using Layeron.App.Session;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReactiveUI.Builder;

namespace Layeron.App.Test;

/// <summary>
/// A moved tool group can stay in the model but take no space. Dock's proportional panel treats an
/// already allocated sibling (proportion 1) as using the whole pane and gives the new sibling 0, so the
/// group is alive and in the layout yet invisible. These tests run a real DockControl layout pass,
/// because the zero is written by the panel, not by the docking model alone.
/// </summary>
[TestClass]
public sealed class DockProportionTests
{
    [ClassInitialize]
    public static void InitializeReactiveUI(TestContext _) =>
        RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();

    public TestContext? TestContext { get; set; }

    [TestMethod]
    [DataRow(DockOperation.Right)]
    [DataRow(DockOperation.Top)]
    public void MovingOnlyToolGroupToEdge_KeepsANonZeroShare(DockOperation operation)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        session.Dispatch(() =>
        {
            var factory = new DockFactory(new SessionManager());
            var root = factory.BuildOrFallback(DefaultLayout.Create());
            var dockControl = new DockControl { Layout = root };
            var window = new Window { Width = 1200, Height = 800, Content = dockControl };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // Reproduce the reported sequence: merge the only other group into this one first.
            var bottom = factory.Find(root, d => d is IToolDock td && td.Alignment == Alignment.Bottom)
                .OfType<IToolDock>().First();
            var right = factory.Find(root, d => d is IToolDock td && td.Alignment == Alignment.Right)
                .OfType<IToolDock>().First();
            var log = factory.FindDockable(root, d => d.Id == DockableIds.Log)!;
            factory.MoveDockable(bottom, right, log, null);
            Dispatcher.UIThread.RunJobs();

            var mainLayout = factory.GetDockable<IDockable>("MainLayout")!;
            var manager = new DockManager(new DockService());
            Assert.IsTrue(
                manager.ValidateDockable(right, mainLayout, DragAction.Move, operation, bExecute: true),
                "the group drop should be accepted");

            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();

            Assert.IsNotNull(factory.FindDockable(root, d => d.Id == DockableIds.Log),
                $"Log must still be reachable after moving the group {operation}.");

            var moved = factory.Find(root, d => d is IToolDock td && td.VisibleDockables?.Contains(log) == true)
                .OfType<IToolDock>().First();
            Assert.AreNotEqual(0, moved.Proportion,
                $"The moved tool group must keep a non-zero share {operation}; otherwise it is in the layout but invisible.");

            var documents = factory.Find(root, d => d is IDocumentDock).OfType<IDocumentDock>().First();
            Assert.AreNotEqual(0, documents.Proportion,
                $"The document area must keep a non-zero share {operation}.");

            TestContext?.WriteLine(
                $"operation={operation} tool={moved.Proportion:F3} documents={documents.Proportion:F3} " +
                $"parent={moved.Owner?.GetType().Name}({moved.Owner?.Id})");

            window.Close();
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    public sealed class HeadlessApp : Application
    {
        public override void Initialize()
        {
            Styles.Add(new Avalonia.Themes.Simple.SimpleTheme());
            Styles.Add(new DockSimpleTheme());
        }
    }
}
