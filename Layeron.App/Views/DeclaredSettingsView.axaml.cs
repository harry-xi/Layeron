using Avalonia.Controls;

namespace Layeron.App.Views;

/// <summary>
/// 声明式设置页的默认宿主渲染：按 <see cref="SettingKind"/> 显示开关 / 数字框 / 文本框 /
/// 下拉框。插件只贡献 <see cref="SettingItem"/> 数据与取值委托，不写任何控件。
/// </summary>
public partial class DeclaredSettingsView : UserControl
{
    public DeclaredSettingsView()
    {
        InitializeComponent();
    }
}

