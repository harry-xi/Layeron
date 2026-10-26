# Your First Plugin

From nothing to a running plugin: a dockable panel that appears in the `插件` (Plugins) menu.

## 1. Create the project

A plugin is an ordinary .NET class library targeting the same framework as the host (`net10.0`):

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <!-- Identity goes into assembly metadata: the host reads it without loading the DLL -->
  <ItemGroup>
    <AssemblyMetadata Include="LayeronPluginId" Value="acme.hello" />
    <AssemblyMetadata Include="LayeronPluginName" Value="Hello Plugin" />
    <AssemblyMetadata Include="LayeronPluginVersion" Value="1.0.0" />
    <AssemblyMetadata Include="LayeronPluginDescription" Value="A minimal example plugin." />
    <AssemblyMetadata Include="LayeronPluginApiVersion" Value="1.0" />
  </ItemGroup>

  <!-- Contracts are compile-time only: the host's copies are used at runtime -->
  <ItemGroup>
    <ProjectReference Include="..\Layeron.Plugin.Abstractions\Layeron.Plugin.Abstractions.csproj" Private="false" />
    <ProjectReference Include="..\Layeron.Big3DArray\Layeron.Big3DArray.csproj" Private="false" />
  </ItemGroup>

</Project>
```

If the panel uses Avalonia controls (the `HelloView` below does), add a **compile-time-only** reference; at runtime the host's copy is used:

```xml
<PackageReference Include="Avalonia" Version="12.1.3" ExcludeAssets="runtime" />
```

## 2. Write the entry class

```csharp
using Layeron.Plugin.Abstractions;

namespace Acme.Hello;

[Plugin(Id)]
public sealed class HelloPlugin : IPlugin
{
    public const string Id = "acme.hello";

    public void Activate(IPluginContext context)
    {
        var services = context.Services;

        // A dockable panel. Its content is created the first time it is opened.
        context.Registry.AddTool(new ToolContribution
        {
            Id = "hello",
            Title = "Hello",
            PreferredDock = DockHint.Right,
            ContentFactory = () => new HelloView(services)
        });

        // A menu entry: a top-level item under the Plugins menu.
        // Use Path = ["folder", "subfolder"] to nest it; omitting Path means top level.
        context.Registry.AddMenu(new MenuContribution
        {
            Header = "Hello plugin",
            CanExecute = () => services.Volumes.Current != null,
            Execute = () => services.Log.Info("Hello from {Plugin}!", context.PluginId)
        });
    }
}
```

Four things to notice:

1. **`[Plugin(Id)]`** marks the single entry class; it must implement `IPlugin` and have a public parameterless constructor. Name, version and description come from assembly metadata.
2. **`Id` must match `LayeronPluginId`** in the assembly metadata, otherwise a diagnostic is logged. The id is the plugin's identity, used for de-duplication and lookup.
3. **`ContentFactory` is lazy**: the panel is created only when first opened. A plugin that is never opened costs nothing.
4. **`CanExecute` is re-evaluated when the menu opens**, so "disabled while there is no data" needs no state of your own.

## 3. Write the panel content

The content can be anything — a hand-built control tree, or a view with a view model. The contract only requires an `object`:

```csharp
using Avalonia.Controls;
using Avalonia.Layout;
using Layeron.Plugin.Abstractions;

namespace Acme.Hello;

internal sealed class HelloView : UserControl, IDisposable
{
    private readonly IPluginServices _services;

    public HelloView(IPluginServices services)
    {
        _services = services;

        var label = new TextBlock { Text = "No data loaded" };
        var button = new Button { Content = "Count voxels" };
        button.Click += OnCount;

        Content = new StackPanel
        {
            Spacing = 8,
            Margin = new Avalonia.Thickness(12),
            Children = { label, button }
        };

        // Subscribing to a host event: it must be detached with the view
        _services.Volumes.Replaced += OnVolumeReplaced;
    }

    private void OnCount(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var volume = _services.Volumes.Current;
        if (volume is null)
            return;

        _services.Log.Info("Volume {X}x{Y}x{Z} = {Count} voxels",
            volume.X, volume.Y, volume.Z, (long)volume.X * volume.Y * volume.Z);
    }

    private void OnVolumeReplaced(long version) =>
        _services.Log.Debug("Volume replaced, version {Version}", version);

    // Called when the panel closes or the plugin unloads: this is where events are detached
    public void Dispose() => _services.Volumes.Replaced -= OnVolumeReplaced;
}
```

::: warning Implementing IDisposable is not optional
The host disposes the `ContentFactory` result when the **panel closes or the plugin unloads**. If the view subscribed to host events, started threads or holds GPU resources, it must finish here — otherwise the plugin's load context cannot be collected and `VerifyUnloaded()` will name your plugin.
:::

## 4. Build and deploy

Two development paths:

| Path | Location | Notes |
|---|---|---|
| **Development directory** | `<app output>\plugins.dev\<plugin id>\` | **Wins over** an installed plugin with the same id; rebuild and verify |
| **Installed directory** | `%LOCALAPPDATA%\Layeron\plugins\<plugin id>\` | For long-term use |

`plugins.dev` taking precedence is deliberate: a locally built plugin **shadows** an installed one with the same id, so iteration never requires uninstalling the old version first.

Copy the plugin's **whole output directory**, not just the single DLL — a plugin may have private dependencies, resolved by its own load context from its folder and `.deps.json`.

Then refresh from `窗口 → 设置 → 插件` (Window → Settings → Plugins) using the reload/rescan entries. Compiled plugins have no "Reload scripts" menu; that entry belongs to the script host.

## 5. Debugging

The log window (`Tools → 日志`) is the primary diagnostic surface: every step of plugin load and unload, `CanExecute` exceptions and extension point conflicts are written there.

Logging from a plugin through `services.Log` goes to the same window and the same file as host logging:

```csharp
services.Log.Info("loaded {Count} files", files.Count);     // named values are fields, not a sentence
services.Log.Error("failed to parse {Path}", ex, path);     // the exception is a separate field
var log = services.Log.ForContext("SourceId", "acme.feed"); // every message carries the context
```

`{Placeholder}` holes in a message template are shown as **structured fields**, so entries can be filtered and copied individually.

## Next

- Why there is no `Shutdown`: [Lifetime & Scopes](/en/plugin/lifecycle)
- Every contribution type: [Tools, Documents & Menus](/en/plugin/contributions)
- Letting other plugins extend you: [Extension Points](/en/plugin/extension-points)
