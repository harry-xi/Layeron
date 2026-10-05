# C# Scripts

A script is for **one-off computation** on the current data: read it, write the result back into the session, and be done.

Scripts and plugins both extend the application, but serve different purposes:

| | Script | Plugin |
|---|---|---|
| Form | A `.csx` source file | A compiled assembly |
| Suitable for | One-off computation, exploration, batch work | Long-lived features |
| Location | Workspace `scripts/` or plugin directory `scripts/` | Plugin directory |

## Opening the script editor

The `插件 → 脚本` menu has three entries, each opening a script window:

| Menu item | Description |
|---|---|
| New script | A blank script document |
| Open script… | Open a `.csx` from disk |
| Reload scripts | Re-scan the plugin directory so newly installed scripts take effect |

Every script window carries its own toolbar — **Save / Save As… / Open… / Run / Cancel** — plus a status line and a progress bar.

Save and run exist only on the script window, because they need one specific script to act on.

## Running

- It runs the editor's **current contents** (saving first is not required) against the **current data**.
- Computation happens in the background, so a long run does not freeze the window.
- One script runs at a time; press "Cancel" or close the window to stop it.
- Compile errors and runtime exceptions appear in the log window; the application does not crash.
- **Masks, volume changes and log entries persist**; menus and panels a script added temporarily disappear when the run ends.

## What a script can do

- **Read the current data**: the volume, the crosshair position, the gray-level histogram, and values in any region.
- **Report**: write to the log, show a message on the status line, report progress, publish a result as a log entry.
- **Write back to the session**: add mask layers, replace the volume.
- **Extend the application**: add menu entries and dockable panels like a plugin, or contribute buttons to another plugin's extension point.

The full list of available members and signatures is in [API Reference → ScriptGlobals](/en/plugin/api-reference#scriptglobals).

::: tip The data may have changed
Fetch the data together with its version (`Snapshot()`) so a mask derived from old data is never published. If the volume has been replaced by the time you call `AddMask`, it is only logged as a warning and the mask is still added.
:::

## Installing a script as a plugin

Put the `.csx` into the **plugin directory's** `scripts/` (for example `%LOCALAPPDATA%\Layeron\plugins\scripts\`, or `<app dir>\plugins.dev\scripts\` during development), then `插件 → 脚本 → 重新加载脚本`.

Once installed, the script's menus and panels appear in the application exactly like a compiled plugin's, and disappear when that plugin is unloaded.

::: tip Two identities
Where a `.csx` lives decides what it is: in a workspace it is **your working script** (only edited and run), in a plugin directory it is **a script installed as a plugin** (activated). Saving alone never causes code to start running.
:::
