# 安装与运行

## 环境要求

| 组件 | 版本 |
|---|---|
| .NET SDK | 10.0（项目目标框架为 `net10.0`） |
| 操作系统 | Windows（当前主要的开发与验证平台） |
| GPU | 不需要。切片浏览与掩膜 3D 模型都使用 CPU 路径 |

## 获取源码并运行

```powershell
git clone https://github.com/harry-xi/Layeron.git
cd Layeron
dotnet run --project Layeron.App
```

一次构建就会带上可用的插件集合（首次构建需要还原 NuGet 依赖，会慢一些）。

## 运行测试

```powershell
dotnet test
```

测试不需要图形环境。

## 目录约定

Layeron 把自己的状态分成**应用状态**与**用户数据**两类，分别放在不同位置：

| 内容 | 位置 | 说明 |
|---|---|---|
| 插件（已安装） | `%LOCALAPPDATA%\Layeron\plugins` | 每装一份长期使用的插件放在这里 |
| 插件（开发构建） | `<应用输出目录>\plugins.dev` | 本地构建的插件，优先于已安装的同名插件 |
| 默认工作区 | `%LOCALAPPDATA%\Layeron\workspaces\Default` | 用户的工作文件（C# 脚本、Python 文件等） |
| 上次工作区 | `%APPDATA%\Layeron\workspace.json` | "上次打开哪个工作区"属于机器状态 |
| 窗口布局 | `%APPDATA%\Layeron\layout.json` | 停靠布局的持久化 |

开发构建会优先于已安装的同名插件，便于迭代验证。

## 首次启动

启动后窗口是空的，按下面的顺序可以快速看到全部功能：

1. `文件 → 生成演示数据`——立刻得到一份可浏览的体数据。
2. 打开 `Tools → 窗宽窗位`，调节灰度下界/上界，观察三个切片视图同步变化。
3. `Tools → 遮罩管理器`查看掩膜面板；用插件“阈值分割”生成一份掩膜。
4. `Tools → 日志`查看日志。

如果你有 DICOM 数据，可以直接用 `文件 → 从数据源加载 → DICOM 序列 (文件夹)` 打开一个序列所在的文件夹。
