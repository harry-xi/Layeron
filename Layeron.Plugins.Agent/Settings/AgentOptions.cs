using System.Text.Json;
using Layeron.Plugin.Abstractions;

namespace Layeron.Plugins.Agent;

/// <summary>
/// The agent's options: plain values behind <see cref="SettingsPageContribution.ValueGetter"/> /
/// <see cref="ValueSetter"/>, which the host calls when it renders the declarative page.
/// <para>
/// Keeping the store behind those two delegates means the chat panel and the settings page read the same
/// values with no notification plumbing between them, and the page can be destroyed and rebuilt freely.
/// </para>
/// <para>
/// Values are written to <c>agent.settings.json</c> in the plugin's data directory as they change, so the
/// endpoint, model and generation parameters survive a restart. The key is the one deliberate exception:
/// it never touches that file - see <see cref="ApiKey"/>.
/// </para>
/// <para>
/// This is configuration only. The transcript is durable too, but it is a record of a conversation rather
/// than a preference: it lives in the workspace's <c>agent/</c> folder (see
/// <see cref="AgentConversationStore"/>), not in this settings file.
/// </para>
/// </summary>
public sealed class AgentOptions
{
    /// <summary>Setting keys, declared once so the page and the reader cannot drift apart.</summary>
    public static class Keys
    {
        public const string Endpoint = "agent.endpoint";
        public const string ApiKey = "agent.api-key";
        public const string Model = "agent.model";
        public const string Temperature = "agent.temperature";
        public const string MaxTokens = "agent.max-tokens";
        public const string SystemPrompt = "agent.system-prompt";
        public const string KeepAttachments = "agent.attach.keep";
        public const string AutoApproveTools = "agent.tools.auto-approve";
    }

    /// <summary>
    /// Environment variable read when no key is typed on the settings page. It is the way to give a machine a
    /// credential without anyone pasting one into a text box - a shell that already exports a key, or a build
    /// agent whose configuration is the secret store.
    /// </summary>
    public const string ApiKeyEnvironmentVariable = "LAYERON_AGENT_API_KEY";

    private const string FileName = "agent.settings.json";

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string? _filePath;
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    private readonly SecureApiKeyStore _secureKeys;

    /// <summary>
    /// Creates the options, restoring what was stored when a data directory is available.
    /// <para>
    /// The host creates the plugin data directory when it builds the plugin context, so the fallback to the
    /// install directory is only for a loader that hands out no data directory at all - writing beside the
    /// assemblies is still better than forgetting the configuration.
    /// </para>
    /// <para>
    /// Having nowhere to write is not an error: a host may give a plugin no directory, and losing a
    /// preference is a smaller problem than refusing to open. A file that cannot be read is treated the same
    /// way - everything still works for this session, only the next restart forgets.
    /// </para>
    /// </summary>
    public AgentOptions(string? dataDirectory = null)
        : this(dataDirectory, secureKeys: null)
    {
    }

    /// <summary>
    /// The full constructor: <paramref name="secureKeys"/> is where a typed key is durably kept. The default
    /// points at the user's real credential store; tests hand in one rooted at a scratch directory. Creation
    /// itself cannot fail - an unusable platform is absorbed by the store, and every lookup then comes back
    /// empty.
    /// </summary>
    internal AgentOptions(string? dataDirectory, SecureApiKeyStore? secureKeys)
    {
        _secureKeys = secureKeys ?? new SecureApiKeyStore();

        if (string.IsNullOrWhiteSpace(dataDirectory))
            return;

        try
        {
            Directory.CreateDirectory(dataDirectory);
            _filePath = Path.Combine(dataDirectory, FileName);
            Load();
        }
        catch (Exception)
        {
            // An unwritable directory costs durability, not function: the values still apply this session.
            _filePath = null;
        }
    }

    /// <summary>Backs the contribution's <c>ValueGetter</c>.</summary>
    public object? Get(string key) => _values.GetValueOrDefault(key);

    /// <summary>Backs the contribution's <c>ValueSetter</c>; the value is durable from here on.</summary>
    public void Set(string key, object? value)
    {
        _values[key] = value;

        // The key is written through to the credential store the moment it is typed, not on the next
        // restart: a value that only becomes durable later is a value the user believes they have
        // saved but have not.
        if (key == Keys.ApiKey)
            PersistApiKey(value);

        Save();
    }

    /// <summary>
    /// OpenAI-compatible base address, e.g. <c>https://api.openai.com/v1</c> or a local runtime's URL.
    /// </summary>
    public string Endpoint => Read(Keys.Endpoint, "https://api.openai.com/v1");

    /// <summary>
    /// Credential for <see cref="Endpoint"/>. The chain is: what the user typed on the settings page this
    /// session, then the key saved in <see cref="SecureApiKeyStore"/> - the OS credential facility (DPAPI
    /// file, keychain or secret service) that a typed key is written through to, so it outlives the
    /// process without ever appearing in the settings file - then <see cref="ApiKeyEnvironmentVariable"/>.
    /// <para>
    /// Empty is a valid configuration, because a local runtime often wants no key, so it is not treated as a
    /// missing value.
    /// </para>
    /// <para>
    /// Clearing the text box and saving deletes the stored key, which is the only way to retire one; the
    /// host renders this key in its ordinary single-line text box, which cannot mask input, so the value is
    /// visible while it is being typed - it is never echoed back from the store on later sessions.
    /// </para>
    /// </summary>
    public string ApiKey
    {
        get
        {
            // A key is text or it is nothing. Falling back to a converted value would let a stray number
            // become the credential that is sent to the provider.
            var typed = _values.GetValueOrDefault(Keys.ApiKey) as string ?? "";
            typed = typed.Trim();
            if (typed.Length > 0)
                return typed;

            // A key the user saved is more recent than a variable that may have been exported months ago,
            // so the environment stays the last resort of the chain.
            var stored = _secureKeys.Load();
            if (!string.IsNullOrWhiteSpace(stored))
                return stored.Trim();

            return EnvironmentApiKey();
        }
    }

    /// <summary>
    /// Reads the environment variable. A host can set variables per plugin load and a locked-down process can
    /// refuse to read the environment at all, so the lookup is allowed to fail into "not configured".
    /// </summary>
    private static string EnvironmentApiKey()
    {
        try
        {
            return Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable)?.Trim() ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>
    /// Writes the typed key through to <see cref="SecureApiKeyStore"/>. A non-empty value replaces whatever
    /// was saved, an emptied text box retires it - that gesture is the delete button the page has no room
    /// for. A non-text value is left alone entirely: it is rejected as a key for this session, so it must
    /// not be able to rewrite - let alone erase - a credential that was saved on purpose.
    /// </summary>
    private void PersistApiKey(object? value)
    {
        if (value is not string text)
            return;

        var trimmed = text.Trim();
        if (trimmed.Length > 0)
            _secureKeys.Save(trimmed);
        else
            _secureKeys.Delete();
    }

    public string Model => Read(Keys.Model, "gpt-4o-mini");

    public double Temperature => Read(Keys.Temperature, 0.7d);

    public long MaxTokens => Read(Keys.MaxTokens, 1024L);

    public string SystemPrompt => Read(Keys.SystemPrompt, "你是 Layeron 的影像分析助手，回答尽量简洁，并优先给出可执行的下一步。");

    /// <summary>Whether the input area keeps its attachments after a successful send.</summary>
    public bool KeepAttachments => Read(Keys.KeepAttachments, false);

    /// <summary>
    /// Whether tool calls that normally wait for a human decision run without asking.
    /// <para>
    /// On by default: a run that stops at every file write or script would need someone watching it, and
    /// the long analyses this panel is for are usually left to finish. Turning it off restores per-call
    /// confirmation for the tools that change something outside the conversation.
    /// </para>
    /// </summary>
    public bool AutoApproveTools => Read(Keys.AutoApproveTools, true);

    private T Read<T>(string key, T fallback)
    {
        var raw = _values.GetValueOrDefault(key);
        return raw switch
        {
            null => fallback,
            T typed => typed,
            // The host's numeric rows hand back long or double depending on the declared kind, so a narrow
            // read has to survive the wider type rather than throw.
            _ => ConvertTo(raw, fallback)
        };
    }

    private static T ConvertTo<T>(object raw, T fallback)
    {
        try
        {
            return (T)Convert.ChangeType(raw, typeof(T));
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private void Load()
    {
        if (_filePath is null || !File.Exists(_filePath))
            return;

        Dictionary<string, JsonElement>? stored;
        try
        {
            stored = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                File.ReadAllText(_filePath), SerializerOptions);
        }
        catch (Exception)
        {
            // A file that cannot be parsed is the user's - or a crash's - to inspect, so it is left where it
            // is. The next save replaces it because a save always writes the whole file, which is what makes
            // starting on the defaults a recoverable state rather than a permanent one.
            return;
        }

        if (stored is null)
            return;

        foreach (var pair in stored)
            _values[pair.Key] = ToClrValue(pair.Value);
    }

    /// <summary>
    /// Converts one stored value back to a plain CLR type.
    /// <para>
    /// A round trip through <c>object</c> would otherwise hand back <see cref="JsonElement"/> - a type the
    /// readers know nothing about, so every string setting would silently fall back to its default the first
    /// time it was read from disk instead of from memory.
    /// </para>
    /// </summary>
    private static object? ToClrValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True or JsonValueKind.False => element.GetBoolean(),
        JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDouble(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        // Arrays and objects have no reader today; keeping the raw text means a value written by a future
        // version is preserved rather than dropped.
        _ => element.GetRawText()
    };

    private void Save()
    {
        if (_filePath is null)
            return;

        try
        {
            // The key is dropped from the copy that goes to disk, not kept out of the in-memory dictionary:
            // the user's typed value must still apply to this session, it just must not outlive it.
            var values = new Dictionary<string, object?>(_values, StringComparer.Ordinal);
            values.Remove(Keys.ApiKey);

            File.WriteAllText(_filePath, JsonSerializer.Serialize(values, SerializerOptions));
        }
        catch (Exception)
        {
            // Losing a preference is not worth an unhandled exception on the UI thread: the value still
            // applies for this session, because it is already in the dictionary.
        }
    }

    /// <summary>
    /// The page the host renders: plain setting rows, no UI code in the plugin. Every key above appears here
    /// exactly once.
    /// </summary>
    public SettingsPageContribution CreateSettingsPage() => new()
    {
        Id = "layeron.agent.settings",
        Title = "Agent",
        Order = 60,
        Tooltip = "AI 智能体的模型、生成参数与附件默认行为",
        ValueGetter = Get,
        ValueSetter = Set,
        Settings =
        [
            new SettingItem
            {
                Key = Keys.Endpoint,
                Title = "服务地址",
                Description = "兼容 OpenAI 的接口基地址，例如 https://api.openai.com/v1 或本地运行时的地址。",
                Kind = SettingKind.Text,
                DefaultValue = "https://api.openai.com/v1"
            },
            new SettingItem
            {
                Key = Keys.ApiKey,
                Title = "API Key",
                Description = "输入即保存到本机凭据库（Windows 为 DPAPI 加密文件，macOS 为钥匙串，Linux 为 Secret Service），重启后自动读取；清空并保存即删除已存密钥。也可留空改用环境变量 LAYERON_AGENT_API_KEY。",
                Kind = SettingKind.Text,
                DefaultValue = ""
            },
            new SettingItem
            {
                Key = Keys.Model,
                Title = "模型",
                Kind = SettingKind.Text,
                DefaultValue = "gpt-4o-mini"
            },
            new SettingItem
            {
                Key = Keys.Temperature,
                Title = "温度",
                Description = "越低越确定；0 表示几乎只取最高概率的输出。",
                Kind = SettingKind.Number,
                Minimum = 0,
                Maximum = 2,
                DefaultValue = 0.7
            },
            new SettingItem
            {
                Key = Keys.MaxTokens,
                Title = "单次回复上限 (token)",
                Kind = SettingKind.Integer,
                Minimum = 64,
                Maximum = 32768,
                DefaultValue = 1024
            },
            new SettingItem
            {
                Key = Keys.SystemPrompt,
                Title = "系统提示",
                Description = "每次对话的最前置说明。",
                Kind = SettingKind.Text,
                DefaultValue = "你是 Layeron 的影像分析助手，回答尽量简洁，并优先给出可执行的下一步。"
            },
            new SettingItem
            {
                Key = Keys.KeepAttachments,
                Title = "发送后保留附件",
                Description = "关闭时，发送成功即清空输入区的附件。",
                Kind = SettingKind.Boolean,
                DefaultValue = false
            },
            new SettingItem
            {
                Key = Keys.AutoApproveTools,
                Title = "自动通过工具调用",
                Description = "开启时，需要确认的工具（写文件、删除文件、执行 Python 代码或脚本）不再逐次询问，直接执行；关闭后恢复逐次确认。",
                Kind = SettingKind.Boolean,
                DefaultValue = true
            }
        ]
    };
}
