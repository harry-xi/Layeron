using Layeron.Plugin.Abstractions;
using Layeron.Plugin.Host;

namespace Layeron.Plugin.Test;

/// <summary>
/// The shared-contract folder is the one place where a name is deliberately owned by a context other
/// than the plugin that ships it. These tests pin the two rules that keep that safe: the app-owned
/// folder wins over the user folder, and the user folder is additive for contracts the app does not
/// ship.
/// </summary>
[TestClass]
public sealed class SharedContractDirectoryTests
{
    [TestMethod]
    public void UserDirectory_IsScannedWhenTheAppDirectoryIsEmpty()
    {
        using var directories = new TestDirectories();
        var contract = CopyTo(directories.User, TestPaths.ScriptHostPluginAssembly);

        var shared = new PluginSharedAssemblies([directories.App, directories.User]);

        Assert.IsTrue(
            shared.Contains(Path.GetFileNameWithoutExtension(contract)),
            "a contract installed only by the user must be loaded from the user's shared folder");
    }

    [TestMethod]
    public void AppDirectory_WinsOverAUserCopyOfTheSameContract()
    {
        using var directories = new TestDirectories();
        CopyTo(directories.App, TestPaths.ScriptHostPluginAssembly);
        CopyTo(directories.User, TestPaths.ScriptHostPluginAssembly);

        var warnings = new List<string>();
        var shared = new PluginSharedAssemblies([directories.App, directories.User], warnings.Add);

        Assert.AreEqual(1, shared.Names.Count, "a duplicate simple name must not create a second contract");
        Assert.IsTrue(
            warnings.Any(message => message.Contains("already provides it", StringComparison.Ordinal)),
            "the ignored user copy must be diagnosable rather than silently dropped");
    }

    [TestMethod]
    public void HostAssemblies_AreNotAcceptedAsSharedContracts()
    {
        using var directories = new TestDirectories();
        var hostAssembly = typeof(IPlugin).Assembly.Location;
        CopyTo(directories.User, hostAssembly);

        var warnings = new List<string>();
        var shared = new PluginSharedAssemblies([directories.App, directories.User], warnings.Add);

        Assert.IsFalse(
            shared.Contains(Path.GetFileNameWithoutExtension(hostAssembly)),
            "a host assembly dropped into the shared folder must not become a second identity");
        Assert.IsTrue(
            warnings.Any(message => message.Contains("Ignoring host assembly", StringComparison.Ordinal)),
            "the misplaced host assembly must be reported");
    }

    private static string CopyTo(string directory, string source)
    {
        var destination = Path.Combine(directory, Path.GetFileName(source));
        File.Copy(source, destination);
        return destination;
    }

    private sealed class TestDirectories : IDisposable
    {
        public TestDirectories()
        {
            Root = TestPaths.CreateTemporaryPluginDirectory();
            App = Path.Combine(Root, "app");
            User = Path.Combine(Root, "user");
            Directory.CreateDirectory(App);
            Directory.CreateDirectory(User);
        }

        public string Root { get; }

        public string App { get; }

        public string User { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: a leaked load context must not make a passing test fail during cleanup.
            }
        }
    }
}
