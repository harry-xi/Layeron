# Plugin System Overview

Layeron's plugin system is built around three concepts:

| Concept | Type | Role |
|---|---|---|
| **Scope** | `IPluginContext` | A plugin's lifetime. Every side effect is registered here; disposing it = unloading the plugin |
| **Contribution surface** | `IPluginRegistry` | What a plugin can add to the host: tools, documents, menus, point contributions, plugin sources |
| **Extension point** | `ExtensionPoint<T>` | A slot for **multi-contributor aggregation** |

## What can be extended

| Capability | API | Consumer |
|---|---|---|
| Dockable tool panel | `Registry.AddTool(...)` | Host dock (edge tool area) |
| Centre document tab | `Registry.AddDocument(...)` | Host dock (document area) |
| Menu entry | `Registry.AddMenu(...)` | Host menu |
| Volume filter | `Registry.AddFilter(...)` | Host filter pipeline |
| Volume loader | `Registry.AddLoader(...)` | `File → Load from data source` |
| Settings page | `Registry.AddSettingsPage(...)` | Settings window |
| Custom extension point | `Registry.DefinePoint<T>(...)` | Other plugins |
| A new plugin format | `Registry.AddPluginSource(...)` | Host plugin manager |
| Optional host capability | `Services.GetService<T>()` | Provided by interface |

## Project structure

| Project | Role | Depends on |
|---|---|---|
| `Layeron.Big3DArray` | Volume and bitmap core types, block-level I/O | nothing |
| `Layeron.Plugin.Abstractions` | Plugin contracts (no UI framework) | Big3DArray |
| `Layeron.Scripting.Abstractions` | Scripting contracts (a second system) | Plugin.Abstractions + Big3DArray |
| `Layeron.Plugin.Host` | Loader kernel: discovery, loading, scopes, point registry | Abstractions |
| `Layeron.App` | Avalonia host implementing `IPluginHostAdapter` | Host + Abstractions |
| `Layeron.Plugins.*` | Sample plugins | Abstractions + Big3DArray (compile-time only) |

**A plugin depends only on the contract assemblies**, declared with `Private="false"`:

```xml
<ProjectReference Include="..\Layeron.Plugin.Abstractions\Layeron.Plugin.Abstractions.csproj" Private="false" />
<ProjectReference Include="..\Layeron.Big3DArray\Layeron.Big3DArray.csproj" Private="false" />
```

::: warning Why Private="false" is required
At runtime those assemblies **must come from the host**. If a plugin folder also ships a copy of `Layeron.Plugin.Abstractions.dll`, the plugin sees a **second `IPlugin`** — a type with the same name that is not equal to the host's, and the failure mode is a silent "type mismatch". UI plugins follow the same rule for Avalonia (`ExcludeAssets="runtime"`).
:::

## Two design principles

### 1. There is no Shutdown

A plugin has **no** `Shutdown` / `Deactivate`. Everything registered in `Activate` is recorded on the `IPluginContext`, and disposing the context rolls it back in reverse registration order.

This is not an omission but a decision: a plugin that has to remember what to clean up will eventually miss an item — and that leak is only discovered after an unload. Reversible scopes make "what to clean up" the framework's responsibility rather than each author's discipline.

### 2. The host holds object and interfaces only

Tool and document content is delivered as `Func<object>`: a `Control` in the Avalonia host, but declared as `object` in the contract assembly, so:

- a plugin may use Avalonia, ReactiveUI and XAML freely — hand-built controls or views with view models;
- the host **never** casts to a plugin type and never references a plugin assembly;
- a plugin can be loaded and unloaded in a **headless host** (tests, batch tools), just without a UI.

## Loading and unloading

```csharp
var adapter = new AppPluginHostAdapter(dock, services, log);   // implemented by the host
var manager = new PluginManager(adapter, new PluginManagerOptions());

manager.LoadAll();          // scan plugin directories and activate
manager.Reload("layeron.threshold");
manager.Unload("layeron.model3d");
manager.VerifyUnloaded();   // weak-reference check: report plugins that stayed alive
```

Each plugin loads in its own **collectible `AssemblyLoadContext`**. After an unload, a weak-reference check reports plugins whose code is still referenced (typically an event handler never detached, or a live thread) — so "unloaded" is a verifiable fact rather than a hope.

Managed leaks aside, views must cooperate: if the object returned by `ContentFactory` implements `IDisposable`, the host disposes it when the panel or tab closes **or the plugin unloads**. That is where a view detaches host events and releases GPU resources — without it, the plugin's load context cannot be collected.

## The three sample plugins

| Plugin | Id | Capability demonstrated |
|---|---|---|
| Threshold Segmentation | `layeron.threshold` | Tool panel + menu + a **custom extension point** other plugins feed |
| Mask 3D Model | `layeron.model3d` | **Document** contribution + rendering |
| C# Script Host | `layeron.scripthost` | A **plugin loader** (`IPluginSource`) + its own editor UI |

All three are ordinary plugins: no privileged path was opened for them.

## Next

- Hands-on: [Your First Plugin](/en/plugin/getting-started)
- Lifetime details: [Lifetime & Scopes](/en/plugin/lifecycle)
- Full interface listing: [API Reference](/en/plugin/api-reference)
