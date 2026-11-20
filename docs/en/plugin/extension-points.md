# Extension Points

An extension point is a **second-order** extension mechanism: the host gives plugins places with tools, documents and menus, and plugins give each other places with extension points.

```csharp
// Definer: publish a slot
var point = context.Registry.DefinePoint<VolumeAction>("layeron.threshold.actions", "Threshold panel extensions");

// Contributor: put something in the slot (possibly another plugin)
context.Registry.Contribute("layeron.threshold.actions", new VolumeAction { /* ... */ });
```

## When to use one

The test is simple: **a single consumer, or multi-contributor aggregation?**

| Scenario | Mechanism | Reason |
|---|---|---|
| Tool panels, documents, menus | Direct attachment | Single consumer (host dock / menu), one-to-one, no ordering or events |
| Filters, slice tools, settings pages, plugin-defined payloads | Extension point | Multiple contributors, needs a list, ordering and add/remove events |

Making a dockable tool panel an extension point would only add ordering, event replay and host-owned point lifetime — cost without benefit — which is why they are not extension points. A slice interaction tool (see below) is different: many contributors, one shared toolbox, so it **is** a point.

## Point methods on IPluginRegistry

```csharp
// Define: returns the same instance when the same id is defined with the same contribution type
ExtensionPoint<T> DefinePoint<T>(string pointId, string? displayName = null);

// Look up: null when not defined
ExtensionPoint<T>? GetPoint<T>(string pointId);

// Contribute: the value is stamped with this plugin's id and removed when the plugin unloads
IDisposable Contribute<T>(string pointId, T value);
```

## ExtensionPoint&lt;T&gt;

| Member | Description |
|---|---|
| `Id` | Stable id |
| `DisplayName` | Display name supplied by the definer |
| `OwnerPluginId` | Id of the defining plugin; `null` for a host-defined point |
| `ContributionType` | The accepted contribution type (rejects mismatched lookups) |
| `Items` | Current contributions in registration order |
| `Count` | Number of current contributions |
| `Added` / `Removed` | Events |
| `RemoveByOwner(pluginId)` | Removes all of a plugin's contributions (called by the framework on unload) |

`Add(value, ownerPluginId?)` also exists, but it **bypasses scoping** and is reserved for the host and tests. Plugins always go through `Registry.Contribute`.

## Contribution is not ownership

This is the most important semantic of extension points:

> **A contribution disappears when the contributing plugin unloads, even if the point belongs to someone else.**

```csharp
// Plugin A defines the point
var point = contextA.Registry.DefinePoint<VolumeAction>("acme.panel.actions");

// Plugin B contributes a button
contextB.Registry.Contribute("acme.panel.actions", new VolumeAction
{
    Id = "acme.preset.lung",
    Header = "Lung preset",
    Execute = services => ApplyLungWindow(services)
});
// → Unload B: the button goes away, the point remains
// → Unload A: the point goes away and B's contribution with it
```

Host-defined points (the filter point, the loader point, the settings page point and the slice tool point) are defined with `OwnerPluginId == null`, so they **do not disappear with any single plugin** — only each contributor's entry is removed.

## Consuming a point

A consumer subscribes to `Added` / `Removed` and `Track`s the subscription on its own scope:

```csharp
public void Activate(IPluginContext context)
{
    var actions = context.Registry.GetPoint<VolumeAction>("layeron.threshold.actions");
    if (actions is null)
        return;   // the provider is not present; degrade silently

    // Contributions that already exist
    UpdateButtons(actions.Items);

    // Later changes; Track guarantees the subscription ends with the plugin
    context.Registry.Track(actions.Subscribe(
        onAdded: action => UpdateButtons(actions.Items),
        onRemoved: action => UpdateButtons(actions.Items)));
}
```

::: tip A definer is usually a consumer too
The plugin defining a point is normally also a consumer of it. The threshold plugin both defines `layeron.threshold.actions` and consumes it — its own panel renders those buttons. Using the same point instance for both makes behaviour uniform, with no separate path for "contributions I made myself".
:::

## Contribution types must be host-standard types

`T` must be a type from the **host (or a contract assembly)**, never a private type of the contributor or definer. The reason is assembly isolation:

> Each plugin loads in its own load context and holds its own copy of a private assembly. If the contribution type came from the definer's assembly, the contributor and the definer would see **two types with the same name that are not equal**.

So an extension point's payload always uses a type from a contract assembly. `VolumeAction` is the concrete product of that rule:

```csharp
public sealed class VolumeAction
{
    public string? Id { get; init; }                          // ordering and de-duplication
    public required string Header { get; init; }               // button caption
    public required Action<IPluginServices> Execute { get; init; }
    public string? Tooltip { get; init; }
    public Func<IPluginServices, bool>? CanExecute { get; init; }
    public int Order { get; init; } = 100;
}
```

It receives `IPluginServices` rather than "an instance of some tool" — so a contributed preset can read the volume and publish masks while **knowing nothing about that tool**, and therefore without referencing its assembly.

## Built-in host points

The host defines these before loading any plugin; plugins only contribute:

| Point id | Contribution type | See |
|---|---|---|
| `layeron.filters.volume` | `IVolumeFilter` | [Volume Filters](/en/plugin/filters) (host executor and runner panel are wired) |
| `layeron.tools.slice` | `SliceToolContribution` | [Slice tools](#slice-tools) below, [API Reference](/en/plugin/api-reference#slice-tools) |
| `layeron.loaders.volume` | `VolumeLoader` | [Volume Loaders](/en/plugin/loaders) |
| `layeron.settings.pages` | `SettingsPageContribution` | [Settings Pages](/en/plugin/settings) |

Contributing through the extension methods is safer because they validate:

```csharp
context.Registry.AddFilter(myFilter);            // validates globally unique id
context.Registry.AddSliceTool(myTool);           // validates globally unique tool id
context.Registry.AddLoader(myLoader);            // validates globally unique id
context.Registry.AddSettingsPage(myPage);        // validates exactly one content form
```

## Slice tools

`ToolPoints.SliceTools` (`"layeron.tools.slice"`) is the host-owned point for **slice interaction tools** — the brush, eraser, lasso, polygon, rectangle, ellipse, magic wand and magnetic lasso the host ships are implementations of the same contract. Like the filter point it is defined before any plugin loads, with `OwnerPluginId == null`: it exists and is **empty** until something contributes, and it survives every contributor's unload.

The payload is `SliceToolContribution`, the descriptor plus the factory that creates the tool:

```csharp
public sealed class SliceToolContribution
{
    public required SliceToolDescriptor Descriptor { get; init; }   // display metadata; lets the toolbox group the tool without creating it
    public required Func<ISliceTool> Factory { get; init; }         // called at most once per session
}
```

`Descriptor.Id` is the tool id and is also the key the toolbox and the per-session instance cache use. It must be **globally unique across built-ins and all plugins**, and it must never change over the tool's lifetime. The factory is called **lazily, once per session**, on first activation; the instance is cached for that session, so a plugin tool's state never outlives the session and never keeps a plugin load context alive. That also means the instance is reused across activations and must handle being reactivated.

Registering works exactly like `AddFilter`:

```csharp
context.Registry.AddSliceTool(new SprayerTool());
```

`AddSliceTool` wraps `Contribute(ToolPoints.SliceTools, ...)` and additionally **validates that the id is not already taken** — a duplicate fails the activation with an explicit message and leaves nothing behind. Prefix your id with your plugin id:

```csharp
public SliceToolDescriptor Descriptor => new()
{
    Id = "acme.sprayer",
    DisplayName = "Sprayer",
    Group = "retouch",
    Order = 100,
    Options =
    [
        new SliceToolOption
        {
            Id = "radius", Label = "Radius",
            Kind = SliceToolOptionKind.Number, Min = 1, Max = 64, NumberDefault = 4
        }
    ]
};
```

The host's `layeron.tools.*` ids are reserved for built-ins. They are not part of the duplicate check (the point only holds contributions), but the catalog resolves a built-in before a contribution with the same id, so a colliding plugin tool would simply never become active.

**Lifecycle and undo:**

- A contributed tool appears in the host toolbox alongside the built-ins, grouped by `Descriptor.Group` and sorted by `Order` inside the group, with a declarative options editor generated from `Descriptor.Options` — numeric, toggle or combo, exactly as for the built-ins. No host code knows the tool's name.
- The toolbox follows the point's `Added` / `Removed` events, so hot reload needs no panel reopen. On unload the contribution disappears; if the removed tool is the active one it is **deactivated first** and its cached instance dropped, then the contribution is removed. The point itself stays.
- All mask writes go through `context.BeginEdit(...)` and the returned `MaskEditScope`; the host owns the before/after snapshots and publishes **one undo command per gesture** on `Dispose`. A plugin tool cannot bypass that channel — the contract has no way to write mask data directly, so `Ctrl+Z` always works.

The contract types are listed in [API Reference → Slice tools](/en/plugin/api-reference#slice-tools); `design-notes/TOOL_SYSTEM.md` describes the interaction design (planes, previews, how 2D gestures become 3D edits).

## A complete example

```csharp
using Layeron.Plugin.Abstractions;

[Plugin("acme.presets")]
public sealed class PresetsPlugin : IPlugin
{
    public void Activate(IPluginContext context)
    {
        // Depending on a point another plugin defines: degrade silently when it is absent.
        var actions = context.Registry.GetPoint<VolumeAction>("layeron.threshold.actions");
        if (actions is null)
        {
            context.Services.Log.Info("Threshold plugin not loaded; preset buttons not registered.");
            return;
        }

        context.Registry.Contribute("layeron.threshold.actions", new VolumeAction
        {
            Id = "acme.preset.bone",
            Header = "Bone window",
            Order = 20,
            Tooltip = "Set the gray range for looking at bone",
            CanExecute = services => services.Volumes.Current != null,
            Execute = services =>
            {
                // Read data straight through host services, without referencing the threshold plugin
                if (services.Volumes.Current is { } v)
                    services.Log.Info("Bone preset applied to {X}x{Y}x{Z}", v.X, v.Y, v.Z);
            }
        });
    }
}
```

Unload `acme.presets` and the button disappears; unload `layeron.threshold` and the whole slot disappears with every contribution. Neither path needs a single line of cleanup code.
