using System.Reflection;
using Layeron.App.Service;
using Layeron.Plugin.Abstractions;
using ReactiveUI;

namespace Layeron.App.ViewModels;

/// <summary>
/// 设置窗口的“常规”页：外观（明暗主题）+ 版本。证明窗口是通用的——插件页只是其中之一，
/// 插件还可以通过 <c>layeron.settings.pages</c> 扩展点追加更多页面。
/// </summary>
public sealed class GeneralSettingsViewModel : ViewModelBase, ISettingsPage
{
    public const string PageId = "layeron.settings.general";

    private bool _isDarkTheme = AppearanceSettings.IsDark;

    public string Id => PageId;

    public string Title => "常规";

    public int Order => 0;

    public object? Content => this;

    /// <summary>深色主题开关：切换立即生效，并由 <see cref="AppearanceSettings"/> 持久化。</summary>
    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        set
        {
            if (_isDarkTheme == value)
                return;

            // ReactiveUI 24 的 RaiseAndSetIfChanged 返回新值，不能拿来判断是否真的变了。
            this.RaiseAndSetIfChanged(ref _isDarkTheme, value);
            AppearanceSettings.Set(value);
        }
    }

    public string AppVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";

    public string Description =>
        $"界面语言等更多设置后续补充。插件可通过 {SettingsPoints.Pages} 扩展点添加更多设置页。";
}

