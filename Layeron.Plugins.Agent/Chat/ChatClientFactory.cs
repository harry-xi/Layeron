using System.ClientModel;
using OpenAI;
using OpenAI.Chat;

namespace Layeron.Plugins.Agent;

/// <summary>
/// Builds the OpenAI-compatible client the agent talks through.
/// <para>
/// Isolated from the session so that everything the network layer needs - a parseable absolute URL and a
/// non-empty model id - is checked in one place before a socket is opened. The chat panel cannot enforce
/// either: its settings rows are generic text boxes, so an endpoint that cannot be turned into a
/// <see cref="Uri"/> only becomes a fact at the moment someone tries to send.
/// </para>
/// </summary>
internal static class ChatClientFactory
{
    /// <summary>
    /// Sent when the user configured no key, because the SDK refuses an empty credential even for a
    /// server that ignores authentication. A local runtime that checks keys will reject this, which is
    /// what the user needs to be told rather than a local "missing key" error that would be wrong for
    /// the deployment they actually have.
    /// </summary>
    private const string UnauthenticatedKey = "-";

    /// <summary>Creates a client for the endpoint and model currently in <paramref name="options"/>.</summary>
    /// <exception cref="InvalidOperationException">The endpoint or model is not usable as configured.</exception>
    public static ChatClient Create(AgentOptions options)
    {
        var endpoint = options.Endpoint.Trim();
        var model = options.Model.Trim();

        if (Reject(endpoint, model) is { } reason)
            throw new InvalidOperationException(reason);

        var key = options.ApiKey.Trim();

        return new ChatClient(
            model,
            new ApiKeyCredential(key.Length > 0 ? key : UnauthenticatedKey),
            new OpenAIClientOptions { Endpoint = new Uri(endpoint, UriKind.Absolute) });
    }

    /// <summary>Why these values cannot be used, or null when they can.</summary>
    public static string? Reject(string endpoint, string model)
    {
        if (endpoint.Length == 0)
            return "未配置服务地址：请在 Agent 设置里填写兼容 OpenAI 的接口基地址。";

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return $"服务地址不是有效的 http(s) 绝对地址：{endpoint}";

        if (model.Length == 0)
            return "未配置模型名：请在 Agent 设置里填写模型。";

        return null;
    }
}
