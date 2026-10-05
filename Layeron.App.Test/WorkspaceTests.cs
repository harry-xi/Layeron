using System.IO;
using Layeron.App.Workspaces;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layeron.App.Test;

/// <summary>
/// Covers the workspace contract: where a first run points, that the layout of a workspace is created on
/// demand, and that the choice survives a restart. The state file and the default root are both redirected
/// so the tests never read or write the real ones.
/// </summary>
[TestClass]
public sealed class WorkspaceTests
{
    [TestMethod]
    public void FirstRun_UsesTheDefaultWorkspace_AndCreatesItsScriptsFolder()
    {
        using var temp = new TempDirectory();
        var defaultRoot = temp.Path("Default");

        var manager = CreateManager(temp, defaultRoot);

        Assert.AreEqual(Path.GetFullPath(defaultRoot), manager.Current.Root);
        Assert.IsTrue(Directory.Exists(manager.Current.ScriptsDirectory),
            "a workspace must be usable immediately, so its scripts folder is created up front");
    }

    [TestMethod]
    public void OpenedWorkspace_IsRememberedAcrossRestarts()
    {
        using var temp = new TempDirectory();
        var defaultRoot = temp.Path("Default");
        var chosen = temp.Path("Chosen");

        CreateManager(temp, defaultRoot).Open(chosen);

        // A second manager stands in for the next launch: it reads the same state file.
        var restarted = CreateManager(temp, defaultRoot);

        Assert.AreEqual(Path.GetFullPath(chosen), restarted.Current.Root,
            "the last workspace must survive a restart, otherwise the user re-picks it every time");
        Assert.IsTrue(Directory.Exists(restarted.Current.ScriptsDirectory));
    }

    [TestMethod]
    public void Switching_RaisesChanged_ButReopeningTheSameFolderDoesNot()
    {
        using var temp = new TempDirectory();
        var manager = CreateManager(temp, temp.Path("Default"));

        var raised = 0;
        manager.Changed += () => raised++;

        manager.Open(temp.Path("Another"));
        Assert.AreEqual(1, raised, "a switch must notify listeners exactly once");

        manager.Open(temp.Path("Another"));
        Assert.AreEqual(1, raised, "re-selecting the active workspace is not a change");
    }

    [TestMethod]
    public void CorruptStateFile_FallsBackToTheDefaultWorkspace()
    {
        using var temp = new TempDirectory();
        var defaultRoot = temp.Path("Default");
        File.WriteAllText(temp.Path("workspace.json"), "{ this is not json");

        var manager = CreateManager(temp, defaultRoot);

        Assert.AreEqual(Path.GetFullPath(defaultRoot), manager.Current.Root,
            "an unreadable workspace file must not stop the app from starting");
    }

    private static WorkspaceManager CreateManager(TempDirectory temp, string defaultRoot) =>
        new(new TestPluginLog(), temp.Path("workspace.json"), defaultRoot);
}
