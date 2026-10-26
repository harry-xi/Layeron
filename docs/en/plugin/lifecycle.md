# Lifetime & Scopes

Layeron's plugin lifetime has exactly one direction: **register, then be rolled back**. There is no `Shutdown`, no `Deactivate`, and no "please remember to clean up" checklist.

## A full lifetime

```
                   ┌─ Discovery (MetadataReader reads assembly metadata, no plugin code runs)
                   │
   Load ───────────┼─ Validate identity and API version
                   │
                   └─ Create a collectible AssemblyLoadContext
                          │
   Activate ──────────────┴─ IPlugin.Activate(context)
                                 │
                                 ├─ context.Registry.AddTool(...)
                                 ├─ context.Registry.AddMenu(...)
                                 ├─ context.Registry.DefinePoint<T>(...)
                                 ├─ context.Registry.Track(...)
                                 └─ context.Services.Log.Info(...)
                                        │
   Run ─────────────────────────────────┘   (contributions live, driving host UI)
                                        │
   Unload ──────────────────────────────┴─ context.Dispose()
                                              ├─ dispose child scopes recursively first
                                              ├─ release this scope's registrations in reverse order
                                              └─ unload the ALC → weak-reference check
```

## IPluginContext: the scope

`IPluginContext` is the only entry point a plugin receives, and it is also its lifetime object:

| Member | Description |
|---|---|
| `PluginId` | The plugin's stable id |
| `PluginName` | Human-readable name, when known |
| `Services` | Host services: volume, masks, preview, logging |
| `Registry` | Contribution surface: `AddTool` / `AddDocument` / `AddMenu` / `DefinePoint` / `Contribute` / `AddPluginSource` / `Track` |
| `CreateChild(name)` | Creates a nested scope |
| `IsDisposed` | Whether it has been disposed; nothing may be registered afterwards |

`IPluginContext : IDisposable` — disposing it is the **framework's** job (during unload); a plugin never calls it itself.

### Reverse rollback

Registered side effects are released in **reverse registration order**, matching the dependency direction: something registered later may depend on something registered earlier.

### Track: attaching any IDisposable to the plugin's life

```csharp
// A timer, a CancellationTokenSource, an event-subscription wrapper...
context.Registry.Track(new Timer(...));   // or your own IDisposable
```

`Track` is the outlet that brings non-registration resources under scope management. Event subscriptions can also be handled more naturally with an extension method:

```csharp
var point = context.Registry.GetPoint<VolumeAction>("some.point");
if (point != null)
    context.Registry.Track(point.Subscribe(
        onAdded: action => UpdateUi(action),
        onRemoved: action => UpdateUi(null)));
```

`Subscribe` returns an unsubscribe handle; `Track` makes it end with the plugin. **Forgetting `Track` is the most common reason a plugin stays referenced — and therefore uncollectable — after unload.**

## Child scopes

`CreateChild` creates a nested scope: a child's contributions can be released **individually and earlier**, and are always released when the parent is disposed.

```csharp
// A group of registrations that must be torn down together, shorter-lived than the plugin
using var scope = context.CreateChild("one-import");
scope.Registry.AddTool(new ToolContribution { Id = "preview", Title = "Import preview", /* ... */ });
// Leaving the using block removes that panel; the plugin's other contributions are untouched
```

This is exactly the mechanism plugin loaders use: discovered plugins become **child scopes** of the loader, so unloading the loader unloads everything it loaded.

## Unload verification

`PluginManagerOptions.VerifyUnload` (default `true`) attempts to collect the plugin's load context after unload and reports the plugins that stayed alive:

```csharp
var leaked = manager.VerifyUnloaded();
foreach (var id in leaked)
    log.Warn("Plugin {Id} is still referenced after unload", id);
```

"Unloaded" is therefore a **verifiable fact**. Typical causes of a leak:

| Cause | Fix |
|---|---|
| Subscribed to a host event and never detached | Detach in the view's `Dispose`, or `Registry.Track(subscription)` |
| A background thread or timer still running | `Registry.Track(theTimer)` |
| A view holds host service references and is never disposed | Make the `ContentFactory` result implement `IDisposable` |
| A static field caches plugin objects | Do not keep plugin instances in static state |
| A plugin type other than a host-standard type was contributed to an extension point | The contribution type's assembly must be loaded by the default context |

::: tip Why a view's Dispose matters
If the object returned by `ContentFactory` implements `IDisposable`, the host disposes it when the panel or tab **closes** and when the plugin **unloads**. That is the only moment a view can detach host events, stop threads and release GPU resources. If it does not, the plugin's load context cannot be collected.
:::

## Hot reload

Reload is "unload + load" with no extra machinery:

```csharp
manager.Reload("acme.hello");     // unload the old one, re-discover and activate
manager.ReloadAllSources();        // re-enumerate every plugin source (scripts included)
```

A development build in `plugins.dev` takes precedence over an installed plugin with the same id, so "edit → build → reload" is a complete iteration loop.

## Headless hosts

Lifetime is independent of UI. A test or batch host only needs a minimal `IPluginHostAdapter` to load, unload and verify plugins completely:

| Concern | Headless host |
|---|---|
| `Activate` | Called normally (`ContentFactory` may never be invoked) |
| Tool / document contributions | Recorded in the adapter; no controls are created |
| UI plugins | Entry-type discovery tolerates types that cannot be materialised, so they still load |
| Scripts | Without `IScriptHostService` the plugin directory's scripts are still discovered and activated, just with no editor |

This is why the plugin documentation keeps insisting "do not touch UI types in `Activate`": put control creation inside `ContentFactory` and the plugin runs naturally in a headless host.
