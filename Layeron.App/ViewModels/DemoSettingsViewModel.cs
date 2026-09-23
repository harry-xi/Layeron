using System;
using System.Collections.Generic;
using Layeron.Plugin.Abstractions;
using ReactiveUI;

namespace Layeron.App.ViewModels;

/// <summary>
/// 设置窗口的“演示”页：由应用本体注册（不走扩展点），展示声明式设置项的全部类型——
/// 开关 / 整数 / 浮点 / 文本 / 下拉框。值存进一个内存字典，经
/// <see cref="SettingsPageContribution.ValueGetter"/> / <see cref="ValueSetter"/> 与行模型互通，
/// 这正是插件贡献声明式页面时走的同一条数据通路。
/// </summary>
public sealed class DemoSettingsViewModel : ViewModelBase, ISettingsPage
{
    public const string PageId = "layeron.settings.demo";

    public const int PageOrder = 5;

    /// <summary>内存值存储：键 → 当前值，充当 getter/setter 的后端。</summary>
    private readonly Dictionary<string, object?> _values = new();

    public DemoSettingsViewModel()
    {
        Contribution = new SettingsPageContribution
        {
            Id = PageId,
            Title = "演示",
            Order = PageOrder,
            Tooltip = "展示全部声明式设置项类型（由应用本体注册）",
            Settings = new List<SettingItem>
            {
                new()
                {
                    Key = "demo.enabled",
                    Title = "启用演示功能",
                    Description = "布尔项：渲染为开关。",
                    Kind = SettingKind.Boolean,
                    DefaultValue = true,
                },
                new()
                {
                    Key = "demo.cache-size",
                    Title = "缓存大小 (MB)",
                    Description = "整数项：渲染为数字框，限制在 0–1024。",
                    Kind = SettingKind.Integer,
                    DefaultValue = 64,
                    Minimum = 0,
                    Maximum = 1024,
                },
                new()
                {
                    Key = "demo.quality",
                    Title = "渲染质量",
                    Description = "浮点项：渲染为数字框，步进 0.1。",
                    Kind = SettingKind.Number,
                    DefaultValue = 0.8,
                    Minimum = 0,
                    Maximum = 1,
                },
                new()
                {
                    Key = "demo.user-name",
                    Title = "用户名",
                    Description = "文本项：渲染为单行文本框。",
                    Kind = SettingKind.Text,
                    DefaultValue = "layeron",
                },
                new()
                {
                    Key = "demo.theme",
                    Title = "主题",
                    Description = "下拉项：从候选值中选择。",
                    Kind = SettingKind.Choice,
                    DefaultValue = "跟随系统",
                    Options = ["跟随系统", "浅色", "深色"],
                },
            },
            ValueGetter = key => _values.GetValueOrDefault(key),
            ValueSetter = (key, value) => _values[key] = value,
        };
    }

    /// <summary>声明式贡献数据；宿主的 <see cref="DeclaredSettingsPageViewModel"/> 据此渲染。</summary>
    public SettingsPageContribution Contribution { get; }

    public string Id => PageId;

    public string Title => "演示";

    public int Order => PageOrder;

    /// <summary>返回声明式渲染模型，与扩展点路径（SettingsWindowViewModel.CreatePage）同构。</summary>
    public object? Content => new DeclaredSettingsPageViewModel(Contribution);
}

