# Loading & Viewing Data

## Supported volumes

Layeron works with scalar volumes whose voxels are 8-bit (0–255) or 16-bit (0–65535) unsigned integers, which covers the large majority of medical and scientific image data.

Loading new data replaces the volume in the current session; dimension-bound annotations such as masks are checked against it.

## Loading data

### Built-in paths

| Menu item | Description |
|---|---|
| `文件 → 加载数据` | Open a `.b3d` volume file (the gzip-compressed native format) |
| `文件 → 从数据源加载 → DICOM 序列 (文件夹)` | Open a folder and read one DICOM series from it |
| `文件 → 从图片创建` | Select a set of images and stack them into a volume |
| `文件 → 生成演示数据` | Generate a synthetic 8-bit demo volume |
| `文件 → 生成 16 位演示数据` | Generate a 16-bit demo volume |
| `文件 → 保存数据` | Save the current volume as `.b3d` |

**About DICOM**: frames are ordered by their position in space and converted with `raw × RescaleSlope + RescaleIntercept`, clamped to `[0, 65535]` — Hounsfield units for CT. Since a CT series spans roughly -1024…3000 while a view starts at the element type's full range, the image opens dark: this is exactly what window/level is for.

A folder may hold a whole study (several series). The reader takes the first series and stops when a second one begins — two series are not one volume.

### Plugin-provided data sources

The `文件 → 从数据源加载` menu lists every available data source: the built-in readers, plus **formats contributed by plugins**. The two are used no differently.

See [Volume Loaders](/en/plugin/loaders).

## Saving data

The current volume can be saved as a `.b3d` file with `文件 → 保存数据`, and reopened later with `文件 → 加载数据`.

## Next

- Mark regions of interest: [Mask Layers](/en/guide/masks)
- Process data in code: [C# Scripts](/en/guide/scripting)
- Teach Layeron your own format: [Volume Loaders](/en/plugin/loaders)
