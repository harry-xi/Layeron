---
layout: home

hero:
  name: Layeron
  text: A desktop workbench for volumetric data
  tagline: Slice viewing, mask editing, C# / Python scripting, and a plugin system that can extend almost anything
  image:
    src: /logo.png
    alt: Layeron
  actions:
    - theme: brand
      text: Get Started
      link: /en/guide/
    - theme: alt
      text: Plugin Development
      link: /en/plugin/

features:
  - title: Three orthogonal slice views
    details: Axial, coronal and sagittal views share one dataset and one crosshair, with window/level and a gray-level histogram.
  - title: Large volumes
    details: 8-bit and 16-bit volumes stored as 64³ blocks, read and written through block-level APIs instead of per-voxel indexing.
  - title: Mask editing
    details: Sparse bitmap mask layers with per-layer colour, opacity and visibility — and 3D surfaces derived from them.
  - title: Plugin-based
    details: Plugins add tool panels, document tabs, menus, volume loaders and settings pages — with leak-free hot reload. The filter contract is ready, pending a host-side driver.
  - title: C# / Python scripting
    details: Run .csx scripts for one-off work and install them as plugins; use the embedded CPython REPL and editor on the current data.
  - title: Extensible extension points
    details: Plugins do not only contribute to the host — they can publish their own extension points for other plugins.
---
