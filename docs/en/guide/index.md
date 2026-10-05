# What is Layeron

Layeron is a desktop workbench for **three-dimensional volumetric data**, built with Avalonia and .NET. It puts browsing, measuring, editing and automation in one window: three orthogonal slice views to look at the data, mask layers to mark regions of interest, and C# scripts and plugins to make repetitive work permanent.

Layeron is extensible: data formats, tool panels, menus and volume processing can all be added through plugins, without changing the application itself.

## What it does

| Capability | Description |
|---|---|
| Multi-planar viewing | Axial (XY), coronal (XZ) and sagittal (YZ) slice views sharing a crosshair and zoom |
| Gray-level display | Window/level adjustment, gray-level histogram, per-slice value readout |
| Mask annotation | Sparse bitmap mask layers with overlay, colour, opacity and visibility |
| Data loading | Built-in DICOM series reader, 8-bit and 16-bit volumes; plugins add more formats |
| 3D preview | Generate an interactive 3D model from a mask |
| Scripting | Use C# scripts for one-off computation on the current data, and install them as plugins |
| Extensible | Add data formats, tool panels, menus and processing through plugins |

## Where to go next

- To get the application running: [Install & Run](/en/guide/getting-started).
- To learn what each panel in the window is: [Interface & Layout](/en/guide/interface).
- To write a plugin: jump to the [plugin overview](/en/plugin/).
- To do one-off computation in code: [C# Scripts](/en/guide/scripting).
