# Identity & Metadata

## Two identities, two purposes

A plugin has two sources of identity, serving different moments:

| Source | Location | Read |
|---|---|---|
| **Assembly metadata** | `AssemblyMetadata` in the `.csproj` | **Before** loading (without running plugin code) |
| **The `[Plugin]` attribute** | On the entry class | **During** loading (to find the entry type) |

```xml
<ItemGroup>
  <AssemblyMetadata Include="LayeronPluginId"          Value="acme.hello" />
  <AssemblyMetadata Include="LayeronPluginName"        Value="Hello Plugin" />
  <AssemblyMetadata Include="LayeronPluginVersion"     Value="1.0.0" />
  <AssemblyMetadata Include="LayeronPluginDescription" Value="A minimal example plugin." />
  <AssemblyMetadata Include="LayeronPluginApiVersion"  Value="1.0" />
</ItemGroup>
```

```csharp
[Plugin(Id)]
public sealed class HelloPlugin : IPlugin
{
    public const string Id = "acme.hello";   // must match the metadata
    public void Activate(IPluginContext context) { /* ... */ }
}
```

`PluginAttribute` carries only the id. Name, version, description and API version come from assembly metadata (`LayeronPluginName` / `Version` / `Description` / `ApiVersion`).

## Why the metadata is in the assembly

The host reads those lines **read-only** with `System.Reflection.Metadata.MetadataReader`: it does not run module initialisers, does not run static constructors, and does not need to resolve the plugin's dependencies. Unlike `MetadataLoadContext`, it does not materialise a reflection object graph or probe dependency paths.

Consequently:

- **A broken or hostile DLL is rejected before it runs** — reading metadata executes no plugin code;
- a DLL without `LayeronPluginId` is skipped outright (logged at DEBUG);
- there is **no `manifest.json`** for a .NET plugin: the metadata is already in the assembly, and a manifest would only create a second source of truth to keep in sync.

::: tip Non-.NET plugins
Candidates such as scripts and interpreters have no assembly metadata; they are discovered by an `IPluginSource`, which creates their `PluginDescriptor.Instance` itself. The host does **not** read a `manifest.json`; whether a loader wants one is entirely that loader's decision.
:::

## PluginAttribute

```csharp
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class PluginAttribute : Attribute
{
    public PluginAttribute(string id);
    public string Id { get; }
}
```

It marks the **single** entry class, which must:

- implement `IPlugin`;
- have a **public parameterless constructor**;
- declare the same `Id` as the metadata (a mismatch is logged; the metadata wins).

## API version

```csharp
public static class PluginApi
{
    public const string Version = "1.0";
}
```

A plugin declares `LayeronPluginApiVersion` in its metadata, stating which version of the contract it was built against. By default (`EnforceApiVersion = true`) the host **refuses candidates whose major version differs**, with an explicit warning:

```
Plugin 'acme.hello' declares API version 2.0, host provides 1.0 - refusing to load.
```

| Case | Behaviour |
|---|---|
| Same major (`1.0` vs `1.1`) | Loads |
| Different major (`1.0` vs `2.0`) | Refused |
| Version not declared | Loads, with a log entry |

`PluginManagerOptions.EnforceApiVersion` turns the check off (mainly for tests).

## PluginDescriptor

The candidate description produced during discovery. **Exactly one of `Instance` and `Location` is set**:

```csharp
public sealed class PluginDescriptor
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? Version { get; init; }
    public string? Description { get; init; }
    public string? ApiVersion { get; init; }

    public string? Location { get; init; }     // assembly candidate: the DLL to load
    public IPlugin? Instance { get; init; }    // in-process candidate: created by the loader
    public string? Origin { get; init; }       // diagnostic origin, e.g. a script path
    public string? LifetimeStyle { get; init; } // lifetime declaration from metadata
}
```

| Form | Who creates the instance | Typical case |
|---|---|---|
| `Location` | The host, in its own collectible context | An ordinary .NET plugin |
| `Instance` | The discovering **plugin loader** itself | Scripts, interpreters, in-process candidates |

## Conventions for plugin ids

| Rule | Notes |
|---|---|
| Stable and unique | Used for de-duplication, lookup and dockable id composition |
| Lowercase, dot-separated recommended | e.g. `layeron.threshold`, `acme.hello` |
| Used as a prefix for contributed point ids | Filter ids must be **globally unique**; prefixing with the plugin id is easiest |

The host composes dockable type ids from it:

```
layeron.tool.plugin.{pluginId}.{toolId}
layeron.document.plugin.{pluginId}.{documentId}
```

So a plugin id ends up inside saved layouts — **renaming it makes panels in old layouts unresolved** (they are skipped, and the layout does not fail).

## Sample plugin metadata

```xml
<AssemblyMetadata Include="LayeronPluginId"          Value="layeron.threshold" />
<AssemblyMetadata Include="LayeronPluginName"        Value="Threshold Segmentation" />
<AssemblyMetadata Include="LayeronPluginVersion"     Value="1.0.0" />
<AssemblyMetadata Include="LayeronPluginDescription" Value="Interactive threshold segmentation producing mask layers." />
<AssemblyMetadata Include="LayeronPluginApiVersion"  Value="1.0" />
```

## Extra declarations for UI plugins

A plugin that needs XAML must make Avalonia's **compile-time** assets available while excluding the **runtime** assets (the host supplies those):

```xml
<PropertyGroup>
  <AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>
</PropertyGroup>

<ItemGroup>
  <!-- ExcludeAssets="runtime": compile-time assets only; the host's copy is used at runtime -->
  <PackageReference Include="Avalonia" Version="12.1.3" ExcludeAssets="runtime" />
  <PackageReference Include="ReactiveUI.Avalonia" Version="12.1.2" ExcludeAssets="runtime" />
</ItemGroup>
```

The versions must match `Layeron.App` — the host and the plugin share one Avalonia runtime.
