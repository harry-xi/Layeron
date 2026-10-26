# Install & Run

## Requirements

| Component | Version |
|---|---|
| .NET SDK | 10.0 (projects target `net10.0`) |
| Operating system | Windows (the current development and verification platform) |
| GPU | Not required. Slice viewing and the mask 3D model both use CPU paths |

## Clone and run

```powershell
git clone https://github.com/harry-xi/Layeron.git
cd Layeron
dotnet run --project Layeron.App
```

An ordinary build yields a working plugin set (the first build restores the NuGet dependencies, so it takes longer).

## Run the tests

```powershell
dotnet test
```

The tests need no graphics environment.

## Directory conventions

Layeron separates its state into **application state** and **user data**, and stores them in different places:

| Content | Location | Notes |
|---|---|---|
| Plugins (installed) | `%LOCALAPPDATA%\Layeron\plugins` | Long-lived plugins live here |
| Plugins (development) | `<app output>\plugins.dev` | Locally built plugins, shadowing installed copies |
| Default workspace | `%LOCALAPPDATA%\Layeron\workspaces\Default` | The user's working files (C# scripts, Python files, …) |
| Last workspace | `%APPDATA%\Layeron\workspace.json` | "Which workspace was open" is machine state |
| Window layout | `%APPDATA%\Layeron\layout.json` | Persisted dock layout |

A development build takes precedence over an installed plugin with the same id, which makes iterating easy.

## First run

The window starts empty. This sequence shows every major feature quickly:

1. `文件 → 生成演示数据` (File → Generate demo data) — instantly gives you a volume to browse.
2. Open `Tools → 窗宽窗位` (Window Level) and adjust the lower/upper gray bounds; all three slice views update together.
3. `Tools → 遮罩管理器` (Mask Manager) opens the mask panel; the Threshold Segmentation plugin produces a mask.
4. `Tools → 日志` (Log) shows the log.

If you have DICOM data, `文件 → 从数据源加载 → DICOM 序列 (文件夹)` (File → Load from data source → DICOM series) opens a series folder directly.
