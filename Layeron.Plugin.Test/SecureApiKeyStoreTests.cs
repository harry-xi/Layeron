using Layeron.Plugins.Agent;

namespace Layeron.Plugin.Test;

/// <summary>
/// Covers the credential store itself, at the layer under the options: a round trip, replacement,
/// deletion, and the one property that makes it worth having - the secret never appears as plaintext
/// anywhere the store writes.
/// <para>
/// The store under test is rooted at a scratch directory, so a real key on the developer's machine is
/// never read or overwritten. On a platform without a file-backed store (a headless Linux session, for
/// instance) the persistence tests report inconclusive rather than failing: the rest of the plugin is
/// built to lose durability, not function, and the tests mirror that.
/// </para>
/// </summary>
[TestClass]
public sealed class SecureApiKeyStoreTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "layeron-agent-test", Guid.NewGuid().ToString("N"), "keys");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private SecureApiKeyStore NewStore() => new(_root);

    [TestMethod]
    public void Save_ThenLoad_RoundTripsTheSecret()
    {
        var store = NewStore();
        if (!store.IsAvailable)
            Assert.Inconclusive("this platform exposes no credential store, so there is nothing to round-trip");

        store.Save("sk-round-trip");

        Assert.AreEqual("sk-round-trip", NewStore().Load(), "a fresh instance must read the saved key back");
    }

    [TestMethod]
    public void Save_ReplacesAnExistingKey()
    {
        var store = NewStore();
        if (!store.IsAvailable)
            Assert.Inconclusive("this platform exposes no credential store, so there is nothing to replace");

        store.Save("sk-old");
        store.Save("sk-new");

        Assert.AreEqual("sk-new", NewStore().Load());
    }

    [TestMethod]
    public void Delete_RemovesTheStoredKey()
    {
        var store = NewStore();
        if (!store.IsAvailable)
            Assert.Inconclusive("this platform exposes no credential store, so there is nothing to delete");

        store.Save("sk-doomed");
        Assert.IsTrue(store.Delete());

        Assert.IsNull(NewStore().Load(), "a deleted key must not come back on the next read");
        Assert.IsFalse(store.Delete(), "deleting twice is reported, not a silent success");
    }

    /// <summary>
    /// The property the whole feature rests on: no file the store writes contains the secret as it was
    /// given. On Windows the store is file-backed (DPAPI-encrypted), so this is a real assertion; on
    /// platforms whose store is a system service there are no files to find and it passes trivially.
    /// </summary>
    [TestMethod]
    public void StoredSecret_NeverAppearsAsPlaintextOnDisk()
    {
        var store = NewStore();
        if (!store.IsAvailable)
            Assert.Inconclusive("this platform exposes no credential store, so there is nothing to write");

        store.Save("sk-not-in-plain-text");

        var written = Directory.Exists(_root)
            ? Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
                .SelectMany(File.ReadAllLines)
                .ToList()
            : [];

        Assert.IsFalse(written.Any(line => line.Contains("sk-not-in-plain-text", StringComparison.Ordinal)),
            $"the secret must never be stored as plaintext; found {written.Count} line(s)");
    }
}
