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
/// This is configuration only. The transcript, the draft and the queued attachments are session state and
/// belong to the panel, not to a settings store.
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
    }

    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    /// <summary>Backs the contribution's <c>ValueGetter</c>.</summary>
    public object? Get(string key) => _values.GetValueOrDefault(key);

    /// <summary>Backs the contribution's <c>ValueSetter</c>.</summary>
    public void Set(string key, object? value) => _values[key] = value;

    /// <summary>
    /// OpenAI-compatible base address, e.g. <c>https://api.openai.com/v1</c> or a local runtime's URL.
    /// </summary>
    public string Endpoint => Read(Keys.Endpoint, "https://api.openai.com/v1");

    /// <summary>
    /// Credential for <see cref="Endpoint"/>. Empty is a valid configuration - a local runtime often
    /// wants no key - so it is not treated as a missing value.
    /// <para>
    /// The value never leaves the process: the settings store is this object, and nothing here writes it
    /// to disk. It is also rendered by the host's ordinary single-line text box, which cannot mask
    /// input, so the key is visible while it is being typed.
    /// </para>
    /// </summary>
    public string ApiKey => Read(Keys.ApiKey, "");

    public string Model => Read(Keys.Model, "gpt-4o-mini");

    public double Temperature => Read(Keys.Temperature, 0.7d);

    public long MaxTokens => Read(Keys.MaxTokens, 1024L);

    public string SystemPrompt => Read(Keys.SystemPrompt, "你是 Layeron 的影像分析助手，回答尽量简洁，并优先给出可执行的下一步。");

    /// <summary>Whether the input area keeps its attachments after a successful send.</summary>
    public bool KeepAttachments => Read(Keys.KeepAttachments, false);

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
                Description = "本机运行时通常留空。输入的密钥会明文显示，且只保存在本次运行的进程内。",
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
            }
        ]
    };
}
