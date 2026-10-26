# Interface & Layout

The main window is a docking layout: three slice views occupy the centre document area, tool panels sit on the left, right and bottom edges, the menu bar is at the top and the status bar at the bottom.

## Menus

| Menu | Contents |
|---|---|
| **文件** (File) | Load data, load from data source (built-in and plugin-provided), save data, generate demo data, generate 16-bit demo data, create from images, workspace |
| **窗口** (Window) | Axial / coronal / sagittal view, reset layout, settings |
| **Tools** | Mask Manager, Annotation Manager, Window Level, Log |
| **插件** (Plugins) | Menu entries added by plugins, each under its own subfolder |

The "load from data source" menu lists every data source currently available (built-in and plugin-provided); the "workspace" menu lists the current workspace and the ways to change it.

## The three slice views

All three views show the same dataset, so adjusting the gray range affects them together.

| View | Plane | Meaning |
|---|---|---|
| **Axial** | XY | Page along Z |
| **Coronal** | XZ | Page along Y |
| **Sagittal** | YZ | Page along X |

Each view shows the current slice information (plane, slice index, voxel size) in its top-left corner.

### Mouse interaction

| Action | Behaviour |
|---|---|
| Wheel | Step forward/backward through slices |
| `Shift` + wheel | Zoom around the pointer |
| Left drag | Pan the view |
| Left double-click | Return to fit-to-window framing |
| Right click | Move the crosshair to that point |
| `Ctrl` + left drag | Paint into the active mask layer |

The crosshair is shared: moving it in one view shows the corresponding position in the other two.

## Tool panels

### Window Level

Adjusts the gray range that **every view** displays:

- **灰度下界 / 灰度上界** (lower/upper gray bound): two sliders whose range follows the element type (0–255 for 8-bit, 0–65535 for 16-bit).
- **重置** (Reset): back to the element type's full range.

The panel also shows the gray-level histogram of the current data, with the range you have selected highlighted on it.

**The lower bound is black and the upper bound is white.** 16-bit data (CT Hounsfield units, for example) often opens dark precisely because it spans -1024…3000 while the view starts at 0 — use window/level and the histogram to find where the data actually is.

### Mask Manager

See [Mask Layers](/en/guide/masks).

### Annotation Manager

See [Annotation Layers](/en/guide/annotations).

### Log

A log window: every entry has a time, a level, a category and a message, and values inside a message are shown as separate fields alongside it — so an entry can be read and copied individually.

File operations, workspace switches, plugin loads and unloads, and script results all land here; plugin logging goes to the same window.

Select a row and press `Ctrl+C`, or right-click to copy it.

## Settings window

`窗口 → 设置` opens the settings window: page navigation on the left, content on the right. Built-in pages:

| Page | Contents |
|---|---|
| General | App version and placeholder text |
| Demo | Declarative-settings demonstration page |
| Plugins | Plugin directory locations, loaded plugins and reload entry points |

Plugins can append their own pages through an extension point — see [Settings Pages](/en/plugin/settings).

## Layout persistence

The layout is saved to `%APPDATA%\Layeron\layout.json` and restored on the next start. `窗口 → 重置布局` returns to the default layout.

If a panel added by a plugin no longer exists when a layout is restored, that panel is skipped and the rest of the layout restores normally.
