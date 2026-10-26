# Workspaces

A workspace is a folder holding **your working files**. It can be copied, backed up and shared as a unit, and opens with the same content on another machine.

## Directory convention

The subfolder layout inside a workspace is owned by the host and its plugins:

| Directory | Contents | Owner |
|---|---|---|
| `<workspace>/scripts` | Your C# scripts (`.csx`) | Created by the host |
| `<workspace>/python` | Your Python files (`.py`) | Created by the Python plugin on demand |

`scripts/` is fixed by the host; language-specific folders such as `python/` are resolved by the corresponding plugin. The host only exposes the workspace root, so adding a scripting language needs no host change.

## Locations

| Item | Location |
|---|---|
| Default workspace | `%LOCALAPPDATA%\Layeron\workspaces\Default` |
| Last used workspace | `%APPDATA%\Layeron\workspace.json` |

"Which workspace was open" is stored in application state, so a workspace folder contains nothing but your working files.

## Switching workspaces

`文件 → 工作区 → 打开工作区…` picks a folder; if it does not exist it is created in place. The menu also offers "open in File Explorer".

The current workspace name is always shown in the bottom-right corner of the status bar.

After switching, the script list follows the new workspace. Plugins are unaffected — they come from the plugin directory, not the workspace.

## The two identities of a script

A script in a workspace and a script in a plugin directory are **not the same thing**:

| Directory | Identity | Behaviour |
|---|---|---|
| `<workspace>/scripts` | Your working scripts | Listed, edited and run against the current data; **never** auto-activated as plugins |
| `<plugin dir>/scripts` | Scripts installed as plugins | Discovered and activated: their menus, tools and contributions appear in the application |

**Installing is a deliberate act**: put the file into the plugin directory's `scripts/`, then `插件 → 脚本 → 重新加载脚本` (Plugins → Script → Reload scripts). A script that only lives in a workspace is never executed on its own.

See [C# Scripts](/en/guide/scripting).
