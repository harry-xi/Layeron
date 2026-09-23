using System.Reflection;
using Layeron.Plugin.Abstractions;

namespace Layeron.App.ViewModels;

/// <summary>
/// 设置窗口的“常规”页：占位说明 + 版本。证明窗口是通用的——插件页只是其中之一，
/// 插件还可以通过 <c>layeron.settings.pages</c> 扩展点追加更多页面。
/// </summary>
public sealed class GeneralSettingsViewModel : ViewModelBase, ISettingsPage
{
    public const string PageId = "layeron.settings.general";

    public string Id => PageId;

    public string Title => "常规";

    public int Order => 0;

    public object? Content => this;

    public string AppVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";

    public string Description =>
        $"常规设置预留（主题、语言等）。插件可通过 {SettingsPoints.Pages} 扩展点添加更多设置页。";
}

