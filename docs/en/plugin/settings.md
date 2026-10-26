# Settings Pages

A plugin can append its own pages to the settings window (`窗口 → 设置`). **The normal path is declarative**: the plugin only describes which settings exist, the host renders them, and the plugin writes no UI code at all.

## Two ways to contribute

`SettingsPageContribution` must set **exactly one** of the following (`Validate()` enforces it):

| Form | Suitable for | Required members |
|---|---|---|
| Declarative settings | Ordinary configuration | `Settings` + `ValueGetter` / `ValueSetter` |
| Custom layout | When the default rendering genuinely is not enough | `ContentFactory` |

## Point and registration

```csharp
public static class SettingsPoints
{
    public const string Pages = "layeron.settings.pages";
}

// The extension method validates the contribution
context.Registry.AddSettingsPage(myPage);
```

The settings point is host-owned, so unloading a plugin removes only its own entry. Built-in pages are: General, Demo, Plugins.

## SettingsPageContribution

| Property | Type | Description |
|---|---|---|
| `Id` | `string` | **Required.** Unique within the plugin, e.g. `acme.options` |
| `Title` | `string` | **Required.** Left-hand navigation title |
| `Order` | `int` | Navigation sort key, lower first; default `100` (built-in pages are 0 / 5 / 10) |
| `Tooltip` | `string?` | Optional hint |
| `Settings` | `IReadOnlyList<SettingItem>?` | Declarative settings |
| `ValueGetter` | `Func<string, object?>?` | Reads the current value by `SettingItem.Key`; returning null shows the default |
| `ValueSetter` | `Action<string, object?>?` | Writes the current value by `Key` |
| `ContentFactory` | `Func<object>?` | Creates custom content (called **lazily once** when the page is first selected; the result is cached) |

## SettingItem and SettingKind

```csharp
public sealed class SettingItem
{
    public required string Key { get; init; }          // value key passed to the getter/setter
    public required string Title { get; init; }        // display title
    public string? Description { get; init; }          // optional description
    public SettingKind Kind { get; init; }             // render kind
    public object? DefaultValue { get; init; }         // shown when the key has no value
    public double? Minimum { get; init; }              // numeric lower bound
    public double? Maximum { get; init; }              // numeric upper bound
    public IReadOnlyList<string>? Options { get; init; }  // choices for Choice
}
```

| `SettingKind` | Host control | Fields used |
|---|---|---|
| `Boolean` | Toggle switch | `DefaultValue` |
| `Integer` | Numeric box with bounds | `Minimum`, `Maximum`, `DefaultValue` |
| `Number` | Floating-point numeric box | `Minimum`, `Maximum`, `DefaultValue` |
| `Text` | Single-line text box | `DefaultValue` |
| `Choice` | Combo box | `Options` |

## Complete example: a declarative page

```csharp
using Layeron.Plugin.Abstractions;

namespace Acme.Settings;

internal sealed class Options
{
    private readonly Dictionary<string, object?> _values = new();

    public object? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;
    public void Set(string key, object? value) => _values[key] = value;
}

internal static class OptionsPage
{
    public static SettingsPageContribution Create(Options options) => new()
    {
        Id = "acme.options",
        Title = "ACME",
        Order = 50,
        Tooltip = "Options for the ACME plugin",

        // ValueGetter / ValueSetter keep reading and writing in the plugin's hands:
        // the host only renders controls and hands the user's input back.
        ValueGetter = options.Get,
        ValueSetter = options.Set,

        Settings =
        [
            new SettingItem
            {
                Key = "enabled",
                Title = "Enabled",
                Description = "When off, the plugin stops responding to anything.",
                Kind = SettingKind.Boolean,
                DefaultValue = true
            },
            new SettingItem
            {
                Key = "threshold",
                Title = "Default threshold",
                Kind = SettingKind.Integer,
                Minimum = 0,
                Maximum = 65535,
                DefaultValue = 128
            },
            new SettingItem
            {
                Key = "interpolation",
                Title = "Interpolation",
                Kind = SettingKind.Choice,
                Options = ["Nearest", "Linear", "Cubic"],
                DefaultValue = "Linear"
            },
            new SettingItem
            {
                Key = "endpoint",
                Title = "Service endpoint",
                Kind = SettingKind.Text,
                DefaultValue = "https://localhost:7443"
            }
        ]
    };
}
```

Register it in `Activate`:

```csharp
public void Activate(IPluginContext context)
{
    var options = new Options();
    context.Registry.AddSettingsPage(OptionsPage.Create(options));
}
```

## Lifetime and cleanup

If the `ContentFactory` result implements `IDisposable`, it is disposed when the **page is removed** — the moment to detach the view from host services. Without it the plugin's load context cannot be collected.

If the host caches your custom page content while the settings window can open and close repeatedly, implement `ISettingsPageContent` (`Detach()`) so the control is removed from its old visual tree when the window closes; otherwise the second open can fail because the control already has a parent.

Declarative pages are rendered by the host, so they have no such concern; only a custom page needs its own teardown.

Pages are also **created lazily**: `ContentFactory` is invoked once, only if the user actually selects that page, and the result is cached. A session that never opens settings pays nothing for any settings page.

## Which form should you use?

Try declarative first. `ContentFactory` is only needed when:

- settings interact (a group of items shown only when "advanced mode" is on);
- you need previews, charts or list editing;
- you want to bind controls directly to your own view model.

Even with a custom layout, keep the values in your own object rather than relying on control state — a page may be destroyed and rebuilt.
