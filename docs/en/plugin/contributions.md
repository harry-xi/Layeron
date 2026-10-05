# Tools, Documents & Menus

These three are **direct attachments**: a single consumer (the host's dock and menu), one-to-one registration, and a handle to undo it. They are deliberately not extension points, because the ordering, event replay and cross-plugin reads an extension point brings would be cost without benefit here.

The three methods on `IPluginRegistry`:

```csharp
IDockableContribution AddTool(ToolContribution tool);               // edge tool area
IDockableContribution AddDocument(DocumentContribution document);   // centre document area
IDisposable           AddMenu(MenuContribution menu);               // menu
```

## Tool panels: ToolContribution

| Property | Type | Description |
|---|---|---|
| `Id` | `string` | **Required.** Unique within the plugin |
| `Title` | `string` | **Required.** Default dock title |
| `ContentFactory` | `Func<object>?` | Creates the content; a `Control` in the host |
| `CanClose` | `bool` | Whether the user may close it without unloading the plugin; default `true` |
| `PreferredDock` | `DockHint` | Preferred dock edge; default `Right` |

`DockHint` values: `Right` (0), `Left` (1), `Bottom` (2).

```csharp
context.Registry.AddTool(new ToolContribution
{
    Id = "threshold",
    Title = "Threshold",
    PreferredDock = DockHint.Right,
    ContentFactory = () => new ThresholdToolView(services, context.PluginId, point)
});
```

## Document tabs: DocumentContribution

| Property | Type | Description |
|---|---|---|
| `Id` | `string` | **Required.** Unique within the plugin |
| `Title` | `string` | **Required.** Default tab title |
| `ContentFactory` | `Func<object>?` | Creates the content |
| `CanClose` | `bool` | Default `true` |

**The difference from a tool panel is only where it lands, not what it is**: a document goes into the centre document area (where the slice views live), so it has no dock hint — a document's place is the document area by definition.

Which to use? Decide by the content's role:

| | Tool | Document |
|---|---|---|
| Role | Supporting panel, parameters, manager | Primary view, long-lived content |
| Examples | Threshold parameters, mask manager, log | Slice views, 3D model, report |

```csharp
// layeron.model3d: the 3D model is a primary view, next to the slices
context.Registry.AddDocument(new DocumentContribution
{
    Id = "mask3d",
    Title = "Mask 3D model",
    ContentFactory = () => new Mask3DView(services)
});
```

## IDockableContribution: undo + open

The handle returned by `AddTool` / `AddDocument` has two capabilities:

```csharp
public interface IDockableContribution : IDisposable
{
    /// Brings the panel/tab to the front, creating it on first use
    void Open();
}
```

- `Dispose()` removes the contribution (the framework does this on unload);
- `Open()` shows it now.

**Why is "open" on the handle rather than a separate host call?** Because where a contribution is shown is knowledge the host owns exclusively. A plugin that wants a menu entry to open its own panel just captures the handle:

```csharp
// Capture the handle → the menu entry opens its own panel, never learning the host's dockable type ids
var editor = context.Registry.AddDocument(new DocumentContribution
{
    Id = "editor",
    Title = "Script",
    ContentFactory = () => new ScriptEditorView(...)
});

context.Registry.AddMenu(new MenuContribution
{
    Path = ["Script"],
    Header = "New script",
    Execute = () => editor.Open()      // focuses it if already open
});
```

The script host plugin is implemented exactly this way.

### Dockable type ids

The host synthesises a stable id per contribution:

| Kind | Format |
|---|---|
| Tool | `layeron.tool.plugin.{pluginId}.{toolId}` |
| Document | `layeron.document.plugin.{pluginId}.{documentId}` |

Those two prefixes let the host route a contribution into the right dock **from the id alone**, so restoring a layout needs no extra metadata. The ids contain no CLR type names, so renaming a type does not silently break saved layouts.

## Menus: MenuContribution

| Property | Type | Description |
|---|---|---|
| `Path` | `IReadOnlyList<string>` | Folder path relative to the Plugins menu, **one segment per folder**; empty by default, meaning top level |
| `Header` | `string` | **Required.** Leaf item caption |
| `Execute` | `Action` | **Required.** Invoked on activation |
| `CanExecute` | `Func<bool>?` | Availability predicate, **re-evaluated when the menu opens** |
| `Order` | `int` | Sort key within the level, lower first; default `100` |

`Path` says where the entry **lives**; `Header` is what it is **called**:

```csharp
context.Registry.AddMenu(new MenuContribution
{
    Path = ["Threshold", "Tools"],   // Plugins → Threshold → Tools → <Header>
    Header = "Reset parameters",
    Order = 10,
    CanExecute = () => services.Volumes.Current != null,
    Execute = () => ResetParameters()
});
```

::: tip What belongs in a menu
Entries that **produce a window**. A menu is application-level, whereas an operation like save or run needs a definite subject — it belongs on that subject's own window toolbar, executed by the window itself. The script host's menu contains only "new script / open script… / reload scripts", while save and run live on the script document's toolbar.
:::

## Lifetime and cleanup

All three contribution kinds are managed by the scope; nothing has to be undone by hand:

```csharp
public void Activate(IPluginContext context)
{
    context.Registry.AddTool(...);     // removed on unload
    context.Registry.AddDocument(...); // removed on unload
    context.Registry.AddMenu(...);     // removed on unload
}
```

If the `ContentFactory` result implements `IDisposable`, the host disposes it when the panel or tab **closes** or the plugin **unloads**:

```csharp
internal sealed class MyPanel : UserControl, IDisposable
{
    public MyPanel(IPluginServices services) { /* subscribe to host events */ }

    public void Dispose()
    {
        // Detach events, stop threads, release GPU resources.
        // Without this the plugin's load context cannot be collected.
    }
}
```
