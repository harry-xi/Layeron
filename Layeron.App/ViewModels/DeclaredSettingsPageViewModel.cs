using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Layeron.Plugin.Abstractions;
using ReactiveUI;

namespace Layeron.App.ViewModels;

/// <summary>
/// 声明式设置页的宿主渲染模型：插件只给出 <see cref="SettingItem"/> 描述与取值委托，
/// 这里按 <see cref="SettingKind"/> 把每条配置渲染成对应控件（开关 / 数字框 / 文本 / 下拉框）。
/// 一般配置完全交给配置界面；只有 <see cref="SettingsPageContribution.ContentFactory"/> 存在时，
/// 插件才接管整个页面的布局与控件。
/// </summary>
public sealed class DeclaredSettingsPageViewModel : ViewModelBase, ISettingsPage
{
    private readonly SettingsPageContribution _contribution;

    /// <summary>仅供 XAML 设计预览。</summary>
    public DeclaredSettingsPageViewModel()
        : this(new SettingsPageContribution
        {
            Id = "design.sample",
            Title = "示例页",
            Settings = new[]
            {
                new SettingItem { Key = "enabled", Title = "启用", Kind = SettingKind.Boolean },
                new SettingItem { Key = "level", Title = "级别", Kind = SettingKind.Integer, Minimum = 0, Maximum = 100 },
            },
        })
    {
    }

    public DeclaredSettingsPageViewModel(SettingsPageContribution contribution)
    {
        _contribution = contribution;

        foreach (var item in contribution.Settings ?? Array.Empty<SettingItem>())
            Rows.Add(new SettingRowViewModel(item, contribution.ValueGetter, contribution.ValueSetter));
    }

    public string Id => _contribution.Id;

    public string Title => _contribution.Title;

    public int Order => _contribution.Order;

    public object? Content => this;

    public ObservableCollection<SettingRowViewModel> Rows { get; } = new();

    /// <summary>设置项是否为空（用于显示空态提示）。</summary>
    public bool HasNoRows => Rows.Count == 0;

    /// <summary>
    /// 一条配置的行模型：值属性随控件编辑写回插件的 <c>ValueSetter</c>，
    /// 初始值从 <c>ValueGetter</c> 读取，缺省落到 <see cref="SettingItem.DefaultValue"/>。
    /// </summary>
    public sealed class SettingRowViewModel : ViewModelBase
    {
        private readonly SettingItem _item;
        private readonly Action<string, object?>? _setter;
        private object? _value;

        public SettingRowViewModel(
            SettingItem item,
            Func<string, object?>? getter,
            Action<string, object?>? setter)
        {
            _item = item;
            _setter = setter;
            _value = Normalize(getter?.Invoke(item.Key) ?? item.DefaultValue);
        }

        public SettingItem Item => _item;

        public string Title => _item.Title;

        public string? Description => _item.Description;

        public bool IsBoolean => _item.Kind == SettingKind.Boolean;

        public bool IsInteger => _item.Kind == SettingKind.Integer;

        public bool IsNumber => _item.Kind == SettingKind.Number;

        public bool IsText => _item.Kind == SettingKind.Text;

        public bool IsChoice => _item.Kind == SettingKind.Choice;

        public double? Minimum => _item.Minimum;

        public double? Maximum => _item.Maximum;

        public IReadOnlyList<string> Options =>
            _item.Options ?? Array.Empty<string>();

        public bool BoolValue
        {
            get => _value as bool? ?? false;
            set => Write(value);
        }

        public long IntValue
        {
            get => _value as long? ?? _value as int? ?? 0;
            set => Write(value);
        }

        public double NumberValue
        {
            get => _value as double? ?? _value as int? ?? _value as long? ?? 0d;
            set => Write(value);
        }

        public string TextValue
        {
            get => _value as string ?? "";
            set => Write(value);
        }

        public string ChoiceValue
        {
            get => _value as string ?? Options.FirstOrDefault() ?? "";
            set => Write(value);
        }

        /// <summary>把插件返回的任意值规整到行模型能显示的类型。</summary>
        private object? Normalize(object? raw)
        {
            if (raw is null)
                return null;
            return _item.Kind switch
            {
                SettingKind.Boolean => raw as bool?,
                SettingKind.Integer => raw is sbyte or byte or short or ushort or int or uint
                    ? Convert.ToInt64(raw)
                    : raw is long or ulong ? Convert.ToInt64(raw) : null,
                SettingKind.Number => raw is sbyte or byte or short or ushort or int or uint
                    or long or ulong or float or double or decimal ? Convert.ToDouble(raw) : null,
                SettingKind.Text or SettingKind.Choice => raw as string,
                _ => null,
            };
        }

        private void Write(object? newValue)
        {
            if (Equals(_value, newValue))
                return;
            _value = newValue;
            this.RaisePropertyChanged(string.Empty);
            try
            {
                _setter?.Invoke(_item.Key, newValue);
            }
            catch (Exception ex)
            {
                // 写回失败不打断输入：把错误冒给日志由宿主统一展示即可。
                System.Diagnostics.Debug.WriteLine($"Setting '{_item.Key}' write failed: {ex.Message}");
            }
        }
    }
}
