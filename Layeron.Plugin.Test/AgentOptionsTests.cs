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
    private string _dataFolder = string.Empty;
    private string? _ambientKey;

    [TestInitialize]
    public void Setup()
    {
        _dataFolder = Path.Combine(Path.GetTempPath(), "layeron-agent-test", Guid.NewGuid().ToString("N"));

        // The machine running the tests may well have a real key exported; a test that asserted on the
        // environment would then fail for a reason that has nothing to do with the code.
        _ambientKey = Environment.GetEnvironmentVariable(AgentOptions.ApiKeyEnvironmentVariable);
        Environment.SetEnvironmentVariable(AgentOptions.ApiKeyEnvironmentVariable, null);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable(AgentOptions.ApiKeyEnvironmentVariable, _ambientKey);

        if (Directory.Exists(_dataFolder))
            Directory.Delete(_dataFolder, recursive: true);
    }

    [TestMethod]
    public void Options_SurviveARestart()
    {
        var first = new AgentOptions(_dataFolder);
        first.Set(AgentOptions.Keys.Endpoint, "http://localhost:11434/v1");
        first.Set(AgentOptions.Keys.Model, "qwen3:8b");
        first.Set(AgentOptions.Keys.Temperature, 0.25d);
        first.Set(AgentOptions.Keys.MaxTokens, 4096L);
        first.Set(AgentOptions.Keys.SystemPrompt, "只回答一句话。");
        first.Set(AgentOptions.Keys.KeepAttachments, true);

        var second = new AgentOptions(_dataFolder);

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
        var options = new AgentOptions(_dataFolder);

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
    /// The key is the one setting that must not become a file on disk: a settings page cannot mask its input,
    /// so a key typed into it is already visible, and leaving a copy behind would outlive the gesture.
    /// </summary>
    [TestMethod]
    public void ApiKey_IsAppliedButNeverWrittenToDisk()
    {
        var options = new AgentOptions(_dataFolder);
        options.Set(AgentOptions.Keys.ApiKey, "sk-typed-into-the-panel");

        Assert.AreEqual("sk-typed-into-the-panel", options.ApiKey, "the typed key must apply to this session");

        var file = Path.Combine(_dataFolder, "agent.settings.json");
        Assert.IsTrue(File.Exists(file), "the other settings are still persisted, so the file must exist");

        var text = File.ReadAllText(file);
        Assert.IsFalse(text.Contains("sk-typed-into-the-panel", StringComparison.Ordinal),
            $"the settings file must not contain the key; it was: {text}");

        // And a restart, which is what the file is for, must not resurrect it.
        Assert.AreEqual("", new AgentOptions(_dataFolder).ApiKey);
    }

    [TestMethod]
    public void ApiKey_FallsBackToTheEnvironmentVariable()
    {
        Environment.SetEnvironmentVariable(AgentOptions.ApiKeyEnvironmentVariable, "sk-from-the-environment");

        Assert.AreEqual("sk-from-the-environment", new AgentOptions(_dataFolder).ApiKey);
    }

    /// <summary>
    /// A value the user typed is more recent than a variable that may have been exported months ago, so it
    /// wins - the environment is the fallback, not an override.
    /// </summary>
    [TestMethod]
    public void ApiKey_TypedValueWinsOverTheEnvironmentVariable()
    {
        Environment.SetEnvironmentVariable(AgentOptions.ApiKeyEnvironmentVariable, "sk-from-the-environment");

        var options = new AgentOptions(_dataFolder);
        options.Set(AgentOptions.Keys.ApiKey, "  sk-typed  ");

        Assert.AreEqual("sk-typed", options.ApiKey, "the typed key wins, and surrounding space is ignored");
    }

    /// <summary>
    /// The key is an ordinary setting value, so it is read the same way every other value is - which means a
    /// stray non-string cannot be allowed to throw and take the whole send with it.
    /// </summary>
    [TestMethod]
    public void ApiKey_IgnoresANonTextValue()
    {
        var options = new AgentOptions(_dataFolder);
        options.Set(AgentOptions.Keys.ApiKey, 42L);

        Assert.AreEqual("", options.ApiKey);
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

        var options = new AgentOptions(_dataFolder);

        Assert.AreEqual("gpt-4o-mini", options.Model);

        options.Set(AgentOptions.Keys.Model, "recovered");
        Assert.AreEqual("recovered", new AgentOptions(_dataFolder).Model, "the next save must repair the file");
    }

    /// <summary>
    /// Every declared key must appear on the page exactly once, or a value becomes durable but unreachable.
    /// </summary>
    [TestMethod]
    public void SettingsPage_DescribesEveryKeyOnce()
    {
        var page = new AgentOptions(_dataFolder).CreateSettingsPage();
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
        var first = new AgentOptions(_dataFolder);
        first.Set(AgentOptions.Keys.MaxTokens, 512L);

        var file = Path.Combine(_dataFolder, "agent.settings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        Assert.AreEqual(JsonValueKind.Number, document.RootElement.GetProperty(AgentOptions.Keys.MaxTokens).ValueKind);

        Assert.AreEqual(512L, new AgentOptions(_dataFolder).MaxTokens);
    }
}
