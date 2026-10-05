# Plugin Loaders

**A plugin can be a plugin loader too.** By registering an `IPluginSource`, a plugin gains the ability to discover and activate other plugins — and everything it discovers becomes a **child scope** of that plugin, so unloading it unloads everything it loaded.

This is how the script host is implemented: an ordinary plugin that wraps each `.csx` file in an `IPlugin` instance. The host required no changes.

## Why it exists

Extending "the set of plugin formats" needs no host change:

| Format | Loader |
|---|---|
| `.dll` | The host's assembly scanner |
| `.csx` (C# scripts) | The `layeron.scripthost` plugin |
| Python / F# / a remote feed | Any third-party plugin, the same way |

## IPluginSource

```csharp
public interface IPluginSource
{
    string Id { get; }                                          // stable, e.g. layeron.scripthost.csharp
    string? Description { get; }                                // for diagnostics
    IEnumerable<PluginDescriptor> Discover(IPluginSourceContext context);
}
```

`Discover` enumerates the candidates the source can currently provide. It returns `PluginDescriptor`s whose `Instance` the loader creates itself (in-process), or whose `Location` points at an assembly the host loads in its own collectible context.

## IPluginSourceContext

Passed to `Discover` so a loader never has to guess the host's directory layout:

```csharp
public interface IPluginSourceContext
{
    string PluginDirectory { get; }                 // root plugin directory the host scans (may not exist)
    string DevelopmentPluginDirectory { get; }      // development builds, winning over released copies
    IReadOnlyCollection<string> LoadedPluginIds { get; }   // already loaded, so a source can skip wasted work
    IPluginLog Log { get; }                          // log sink scoped to this source
}
```

## Registering

```csharp
context.Registry.AddPluginSource(new MySource());
```

It returns an `IDisposable`: dispose it and everything the source provided unloads. The host does this automatically when the plugin unloads.

After registration the host calls `Discover` at an **appropriate moment** and activates the results. It does not complete synchronously before `Activate` returns — candidates are queued and processed by `RunPendingSources()`, so load order does not become a reasoning hazard based on "who registered first".

## Reference implementation: the script source

```csharp
internal sealed class ScriptPluginSource : IPluginSource
{
    public string Id => "layeron.scripthost.csharp";
    public string? Description => "C# script plugins (*.csx)";

    // Development directory first: a local copy shadows a released script of the same
    // name, mirroring what the host does for assemblies.
    public IReadOnlyList<string> ResolveScriptDirectories(IPluginSourceContext context) =>
    [
        ScriptPaths.PluginScriptsDirectory(context.DevelopmentPluginDirectory),
        ScriptPaths.PluginScriptsDirectory(context.PluginDirectory)
    ];

    public IEnumerable<PluginDescriptor> Discover(IPluginSourceContext context)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in ResolveScriptDirectories(context))
        {
            if (!Directory.Exists(directory))
                continue;

            foreach (var file in Directory
                         .EnumerateFiles(directory, "*" + ScriptPaths.Extension, SearchOption.TopDirectoryOnly)
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileNameWithoutExtension(file);

                // A development copy wins
                if (!seen.Add(name))
                    continue;

                // Candidates are created in-process: the script host owns this code, so there
                // is no reason to create a load context for a script it compiles itself.
                yield return new PluginDescriptor
                {
                    Id = $"script:{name}",
                    Name = $"Script: {name}",
                    Version = File.GetLastWriteTimeUtc(file).ToString("u"),
                    Description = file,
                    Origin = file,
                    Instance = new ScriptPlugin(file, context.Log)
                };
            }
        }
    }
}
```

## Writing your own loader

### 1. Decide where candidates come from

`Discover` may do anything: scan a folder, read a manifest, query a service, interpret a script. Only two constraints:

- it should be **fast** — it runs on every reload, so keep heavy initialisation out of it (that belongs in `IPlugin.Activate`);
- it should **respect `LoadedPluginIds`** and skip candidates it knows will be de-duplicated.

### 2. Decide who creates the instance

| Your situation | Choose | Notes |
|---|---|---|
| The candidate is a managed assembly you want isolated | `Location` | The host loads it in a collectible context of its own |
| The loader itself interprets/compiles the code | `Instance` | The loader owns the code; no extra context is needed |

The script host uses `Instance`, because the assembly Roslyn compiles is loaded by the **default context**, and creating a context for it from the script host would be pointless.

### 3. Make Discover idempotent

`Discover` may be called more than once (reload, re-scan). Use `context.LoadedPluginIds` and your own memory to avoid producing the same candidate twice.

### 4. Child scopes are automatic

A discovered plugin **automatically** becomes a child scope of the loader:

```
layeron.scripthost (plugin)
  ├─ script:hello (child scope)
  ├─ script:stats (child scope)
  └─ script:batch (child scope)

Unloading layeron.scripthost → all three scripts unload with it
```

## The host side: PluginManager

If you are writing your own host (tests, batch tools), the entry point is `PluginManager`:

```csharp
public sealed class PluginManagerOptions
{
    public string PluginDirectory { get; set; }            // default %LOCALAPPDATA%\Layeron\plugins
    public string DevelopmentPluginDirectory { get; set; } // default <app dir>\plugins.dev
    public bool EnforceApiVersion { get; set; } = true;
    public bool VerifyUnload { get; set; } = true;
}

var manager = new PluginManager(adapter, new PluginManagerOptions());
```

| Method | Effect |
|---|---|
| `LoadAll()` | Scan plugin directories and run every source; returns newly loaded ids |
| `LoadDirectory(path)` | Scan a specific directory |
| `LoadAssembly(path)` | Load a single assembly |
| `LoadInProcess(plugin, id, ...)` | Activate an in-process instance |
| `RunPendingSources()` | Process plugin sources awaiting discovery |
| `Reload(pluginId)` | Unload then load again |
| `Unload(pluginId)` | Unload |
| `UnloadAll()` | Unload everything |
| `ReloadSource(sourceId)` | Re-enumerate one source |
| `ReloadAllSources()` | Re-enumerate every source |
| `VerifyUnloaded()` | Weak-reference check; returns ids of plugins still alive |
| `Plugins` / `LoadedPluginIds` / `Changed` / `PointRegistry` | State and events |

### The host adapter

`PluginManager` references no UI framework; "where a contribution is shown" is supplied by the host through `IPluginHostAdapter`:

```csharp
public interface IPluginHostAdapter
{
    IPluginServices Services { get; }
    IPluginLog Log { get; }

    IDockableContribution AttachTool(string pluginId, ToolContribution tool);
    IDockableContribution AttachDocument(string pluginId, DocumentContribution document);
    IDisposable AttachMenu(string pluginId, MenuContribution menu);
}
```

`Attach*` must return a handle that removes the contribution; on unload the manager disposes those handles in **reverse order**.

Implementing an adapter — even one that creates no UI at all — lets the plugin system run fully in a headless environment, which is how `TestHostAdapter` in `Layeron.Plugin.Test` tests it.
