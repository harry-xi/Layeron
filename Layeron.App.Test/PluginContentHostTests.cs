using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Layeron.App.Controls;
using Layeron.App.Plugins;
using Layeron.App.Views;

namespace Layeron.App.Test;

/// <summary>
/// A plugin supplies a control, not a view model, so a document tab and the floating window it is
/// dragged into are two views of the same control. Avalonia allows a control one visual parent, and
/// Dock keeps the source view alive until its deferred presenter has swapped, so the second view
/// used to fail during layout with "already has a visual parent".
/// </summary>
[TestClass]
public sealed class PluginContentHostTests
{
    [TestMethod]
    public void Floating_a_plugin_document_moves_its_control_to_the_new_view()
    {
        RunOnUiThread(() =>
        {
            var pluginControl = new Border();
            var document = new PluginDocumentViewModel("Mask 3D", "test.model3d", () => pluginControl);

            var (sourceView, sourceWindow) = ShowDocument(document);
            Assert.AreSame(sourceView, pluginControl.FindAncestorOfType<PluginDocumentView>());

            // Dock builds a second PluginDocumentView for the floating window while the source
            // tab's view is still alive.
            var (floatingView, floatingWindow) = ShowDocument(document);

            // The same control moved to the new view; the old one released it instead of trying to
            // keep a second copy alive.
            Assert.AreSame(floatingView, pluginControl.FindAncestorOfType<PluginDocumentView>());
            Assert.IsNull(sourceView.FindDescendantOfType<PluginContentHost>()?.Content,
                "the source view must release the control it no longer owns");
            Assert.AreSame(pluginControl, floatingView.FindDescendantOfType<Border>());

            sourceWindow.Close();
            floatingWindow.Close();
        });
    }

    private static (PluginDocumentView View, Window Window) ShowDocument(PluginDocumentViewModel document)
    {
        var view = new PluginDocumentView { DataContext = document };
        var window = new Window
        {
            Width = 320,
            Height = 240,
            Content = view
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (view, window);
    }

    private static void RunOnUiThread(Action action)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        session.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>Minimal application: the control host needs a ContentControl theme to have a presenter.</summary>
    public sealed class HeadlessApp : Application
    {
        public override void Initialize()
        {
            Styles.Add(new Avalonia.Themes.Simple.SimpleTheme());
        }
    }
}
