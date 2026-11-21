using System.Text.Json;
using Layeron.Plugins.Agent;

namespace Layeron.Plugin.Test;

/// <summary>
/// Covers the agent's durable configuration: what survives a restart, what is deliberately kept out of the
/// file, and where the key comes from.
/// <para>
/// The options are exercised directly rather than through the panel, because that is the whole point of
/// putting them behind <c>ValueGetter</c> / <c>ValueSetter</c>: the decisions - a key that must not be
/// written, a missing file that must not be fatal - are settled without a UI, a socket or an agent backend.
/// </para>
/// </summary>
[TestClass]
public sealed class AgentOptionsTests
{
    private string _root = string.Empty;
    private string _dataFolder = string.Empty;
    private string? _ambientKey;

    [TestInitialize]
    public void Setup()
    {
        // Both stores get their own scratch tree: the settings file so one test cannot read another's
        // leftovers, and the credential store so a test never touches the user's real saved key.
        _root = Path.Combine(Path.GetTempPath(), "layeron-agent-test", Guid.NewGuid().ToString("N"));
        _dataFolder = Path.Combine(_root, "data");

        // The machine running the tests may well have a real key exported; a test that asserted on the
        // environment would then fail for a reason that has nothing to do with the code.
        _ambientKey = Environment.GetEnvironmentVariable(AgentOptions.ApiKeyEnvironmentVariable);
        Environment.SetEnvironmentVariable(AgentOptions.ApiKeyEnvironmentVariable, null);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable(AgentOptions.ApiKeyEnvironmentVariable, _ambientKey);

        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>The options under test, with the credential store pointed at the scratch tree.</summary>
    private AgentOptions NewOptions() => new(_dataFolder, new SecureApiKeyStore(KeyStoreRoot));

    private string KeyStoreRoot => Path.Combine(_root, "keys");

    [TestMethod]
    public void Options_SurviveARestart()
    {
        var first = NewOptions();
        first.Set(AgentOptions.Keys.Endpoint, "http://localhost:11434/v1");
        first.Set(AgentOptions.Keys.Model, "qwen3:8b");
        first.Set(AgentOptions.Keys.Temperature, 0.25d);
        first.Set(AgentOptions.Keys.MaxTokens, 4096L);
        first.Set(AgentOptions.Keys.SystemPrompt, "只回答一句话。");
        first.Set(AgentOptions.Keys.KeepAttachments, true);

        var second = NewOptions();

        Assert.AreEqual("http://localhost:11434/v1", second.Endpoint);
        Assert.AreEqual("qwen3:8b", second.Model);
        Assert.AreEqual(0.25d, second.Temperature);
        Assert.AreEqual(4096L, second.MaxTokens);
        Assert.AreEqual("只回答一句话。", second.SystemPrompt);
        Assert.IsTrue(second.KeepAttachments);
    }

    [TestMethod]
    public void Options_WithoutAStoreYet_UseTheDropInDefaults()
    {
        // Nothing was ever written, so the panel must offer a usable configuration rather than blanks.
        var options = NewOptions();

        Assert.AreEqual("https://api.openai.com/v1", options.Endpoint);
        Assert.AreEqual("gpt-4o-mini", options.Model);
        Assert.AreEqual("", options.ApiKey);
    }

    [TestMethod]
    public void Options_WithoutADataDirectory_StillWorkInMemory()
    {
        var options = new AgentOptions();

        options.Set(AgentOptions.Keys.Model, "in-memory-only");

        Assert.AreEqual("in-memory-only", options.Model);
    }

    /// <summary>
    /// The settings file is still no place for the key: a settings page cannot mask its input, so a key
    /// typed into it is already visible, and a plaintext copy would outlive the gesture. What changed is
    /// where the key does live - the credential store, which a restart reads back.
    /// </summary>
    [TestMethod]
    public void ApiKey_IsAppliedAndPersistedInTheCredentialStore()
    {
        var options = NewOptions();
        options.Set(AgentOptions.Keys.ApiKey, "sk-typed-into-the-panel");

        Assert.AreEqual("sk-typed-into-the-panel", options.ApiKey, "the typed key must apply to this session");

        var file = Path.Combine(_dataFolder, "agent.settings.json");
        Assert.IsTrue(File.Exists(file), "the other settings are still persisted, so the file must exist");

        var text = File.ReadAllText(file);
        Assert.IsFalse(text.Contains("sk-typed-into-the-panel", StringComparison.Ordinal),
            $"the settings file must not contain the key; it was: {text}");

        // A restart does keep the key now - read back from the credential store, not from the file.
        Assert.AreEqual("sk-typed-into-the-panel", NewOptions().ApiKey);
    }

    /// <summary>An emptied text box is the gesture that retires a stored key; there is no delete button.</summary>
    [TestMethod]
    public void ApiKey_ClearedInputRemovesTheStoredKey()
    {
        var options = NewOptions();
        options.Set(AgentOptions.Keys.ApiKey, "sk-once-saved");
        Assert.AreEqual("sk-once-saved", NewOptions().ApiKey, "the key was just saved, so a restart reads it back");

        options.Set(AgentOptions.Keys.ApiKey, "   ");

        Assert.AreEqual("", NewOptions().ApiKey, "clearing the input must remove the stored key, not keep it");
    }

    [TestMethod]
    public void ApiKey_FallsBackToTheEnvironmentVariable()
    {
        Environment.SetEnvironmentVariable(AgentOptions.ApiKeyEnvironmentVariable, "sk-from-the-environment");

        Assert.AreEqual("sk-from-the-environment", NewOptions().ApiKey);
    }

    /// <summary>
    /// A value the user typed is more recent than a variable that may have been exported months ago, so it
    /// wins - the environment is the fallback, not an override.
    /// </summary>
    [TestMethod]
    public void ApiKey_TypedValueWinsOverTheEnvironmentVariable()
    {
        Environment.SetEnvironmentVariable(AgentOptions.ApiKeyEnvironmentVariable, "sk-from-the-environment");

        var options = NewOptions();
        options.Set(AgentOptions.Keys.ApiKey, "  sk-typed  ");

        Assert.AreEqual("sk-typed", options.ApiKey, "the typed key wins, and surrounding space is ignored");
    }

    /// <summary>
    /// The key is an ordinary setting value, so it is read the same way every other value is - which means a
    /// stray non-string cannot be allowed to throw and take the whole send with it. Nor may it touch the
    /// credential store: it was rejected as a key, so it must not become one - or erase one that was saved
    /// on purpose.
    /// </summary>
    [TestMethod]
    public void ApiKey_IgnoresANonTextValue()
    {
        var options = NewOptions();
        options.Set(AgentOptions.Keys.ApiKey, "sk-saved-on-purpose");
        options.Set(AgentOptions.Keys.ApiKey, 42L);

        Assert.AreEqual("sk-saved-on-purpose", options.ApiKey, "the typed key still wins over the store");
        Assert.AreEqual(42L, options.Get(AgentOptions.Keys.ApiKey), "the stray value itself is kept as typed");
        Assert.AreEqual("sk-saved-on-purpose", NewOptions().ApiKey, "the stored key must survive the stray write");
    }

    /// <summary>
    /// A file the plugin did not write, or one truncated by a crash, must not stop the plugin from loading:
    /// the defaults are a working configuration and the next save replaces the file.
    /// </summary>
    [TestMethod]
    public void UnreadableSettingsFile_FallsBackToDefaultsInsteadOfFailing()
    {
        Directory.CreateDirectory(_dataFolder);
        File.WriteAllText(Path.Combine(_dataFolder, "agent.settings.json"), "{ this is not json");

        var options = NewOptions();

        Assert.AreEqual("gpt-4o-mini", options.Model);

        options.Set(AgentOptions.Keys.Model, "recovered");
        Assert.AreEqual("recovered", NewOptions().Model, "the next save must repair the file");
    }

    /// <summary>
    /// Every declared key must appear on the page exactly once, or a value becomes durable but unreachable.
    /// </summary>
    [TestMethod]
    public void SettingsPage_DescribesEveryKeyOnce()
    {
        var page = NewOptions().CreateSettingsPage();
        var keys = typeof(AgentOptions.Keys)
            .GetFields()
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        var declared = page.Settings!.Select(s => s.Key).ToList();

        CollectionAssert.AreEquivalent(keys, declared, "the page and the reader must not drift apart");
    }

    /// <summary>
    /// The stored shape is an implementation detail, but "a number comes back as a number" is not: a
    /// <see cref="JsonElement"/> leaking out of the load path would silently reset every setting it touched.
    /// </summary>
    [TestMethod]
    public void ReloadedValues_ArePlainClrTypes()
    {
        var first = NewOptions();
        first.Set(AgentOptions.Keys.MaxTokens, 512L);

        var file = Path.Combine(_dataFolder, "agent.settings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        Assert.AreEqual(JsonValueKind.Number, document.RootElement.GetProperty(AgentOptions.Keys.MaxTokens).ValueKind);

        Assert.AreEqual(512L, NewOptions().MaxTokens);
    }
}
