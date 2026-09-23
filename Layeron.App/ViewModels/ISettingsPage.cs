namespace Layeron.App.ViewModels;

/// <summary>
/// 设置窗口中的一页：左侧导航显示 <see cref="Title"/>，右侧显示 <see cref="Content"/>。
/// 内置页的 <see cref="Content"/> 通常返回自己（由视图定位器解析出对应视图）；
/// 插件贡献页返回工厂创建的控件。排序先按 <see cref="Order"/>，再按标题。
/// </summary>
public interface ISettingsPage
{
    /// <summary>稳定 Id，用于直接打开指定页面。</summary>
    string Id { get; }

    /// <summary>导航标题。</summary>
    string Title { get; }

    /// <summary>排序键，越小越靠前。</summary>
    int Order { get; }

    /// <summary>右侧显示的内容。</summary>
    object? Content { get; }
}

