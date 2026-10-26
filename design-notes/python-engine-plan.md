# PythonRunner 实现记录

> 状态：已实现并随应用发布。本文替代早期的“调研结论与实施计划”，只描述当前代码的实际行为。
>
> 代码入口：`Layeron.Plugins.PythonRunner/`
> 插件与宿主架构见 [`PLUGIN_SYSTEM.md`](PLUGIN_SYSTEM.md)。

---

## 1. 定位

`Layeron.Plugins.PythonRunner` 是一个**常驻插件**，提供：

- 内嵌 CPython 解释器（pythonnet + `Python.Included`）。
- 交互式 REPL（底部工具面板）。
- Python 文件编辑器（每个 `.py` 一个文档标签页）。
- 设置窗口里的 Python 环境页（解释器 / numpy / pip 状态、安装/卸载包）。
- 只读数据 API：当前体数据、ROI、切片、直方图、掩膜、预览。
- 跨插件服务 `IPythonRunnerService`。

它**不提供** `IPluginSource`：工作区里的 `.py` 文件是工作文档，不会被安装成插件。C# `.csx` 的“安装为插件”由 `Layeron.Plugins.ScriptHost` 负责，两者边界不同。

---

## 2. 生命周期与部署

| 项 | 行为 |
|---|---|
| 插件生命周期 | 应用级。pythonnet 初始化后不能在进程内安全重建解释器，因此插件不参与热卸载/重建。 |
| 首次激活 | **不部署、不启动解释器**。激活只创建 `PythonSession`、注册 REPL / 文档 / 设置页。 |
| 首次使用 | 打开 REPL、运行脚本或打开设置页时才调用 `EnsureReadyAsync`。 |
| 部署位置 | `<插件目录>/python/`（`Installer.InstallDirectory`），随插件自包含。 |
| 解释器 | 由 `Python.Included` 部署；`Installer.PYTHON_VERSION` 为实际版本标签。 |
| NumPy | 随程序集嵌入的 `numpy-1.23.5-cp311-cp311-win_amd64.whl`，解包到解释器 `Lib`。 |
| pip | **不隐式安装**。只有用户点“初始化 pip”或插件显式调用 `BootstrapPipAsync` 才通过 `get-pip.py` 安装。 |
| 启动校验 | `InterpreterAvailable` / `NumpyAvailable` / `PipAvailable` 用文件探针报告状态。 |

部署失败会记录到插件日志，REPL/设置页显示失败状态；不会阻塞应用启动。

---

## 3. 线程模型

| 环节 | 线程 |
|---|---|
| 插件激活、REPL/编辑器 UI、预览发布 | UI 线程 |
| 解释器求值、部署、pip 子进程 | 线程池（`Task.Run`），由 `SemaphoreSlim` 串行化 |
| 数据写回（`AddMask` / `ReplaceVolume` / 通知重绘） | 通过 `UiInvoke`（`Dispatcher.UIThread.Invoke`）回 UI 线程 |
| 输出回调 | 解释器线程 → 宿主 sink → UI |

关键点：

- **没有专用引擎线程**。Python 的 GIL 已经串行化进入解释器的线程；引擎用 `SemaphoreSlim` 加一层“同一时刻只有一次求值”的约束。
- **求值必须 offload 到线程池**。否则 `await` 一个已经同步跑完的 Task 不会让出 UI 线程，脚本会把窗口冻结。
- **GIL 不跨 await 持有**。`EvalAsync` 在同步段取 GIL，返回前释放。
- **UI 线程不碰 `PyObject`**。`PythonBridge` 的写调用只操作托管状态；Python 对象在脚本线程创建/转换。

---

## 4. 解释器边界

`PythonSession` 负责：

- 部署与启动解释器。
- 安装 `bootstrap.py`：替换 `sys.stdout` / `sys.stderr`、安装 `displayhook` 与 REPL 提示符。
- 安装 `layeron.py` 数据 API 模块。
- 启动会话时把 `ModulePath`（当前工作区 `python/` 目录）插到 `sys.path` 最前，使脚本可以 import 同目录的辅助模块。
- 维护最近输出、取消标志、运行时版本。
- `ResetSessionAsync`：丢弃当前 Python 全局命名空间并重建，不重启进程/解释器。

`PythonBridge` 负责数据边界：

- `info` / `read_roi` / `read_xy_slice` / `read_xz_slice` / `read_yz_slice` / `histogram` / `crosshair`。
- `set_volume` / `mask_create` / `mask_add` / `mask_set_slice` / `mask_publish`。
- 所有读取都是**拷贝**，不把主卷的可变视图交给脚本。
- 越界/维度/元素类型不匹配会拒绝并给出原因，而不是静默截断。

---

## 5. Python API 面

脚本里使用：

```python
import layeron

layeron.info()                 # {loaded, shape(z,y,x), dtype, x, y, z, version}
layeron.crosshair()
layeron.histogram()
layeron.read_roi(x, y, z, sx, sy, sz)   # [sz, sy, sx]
layeron.read_xy_slice(z)                 # [y, x]
layeron.read_xz_slice(y)                 # [z, x]
layeron.read_yz_slice(x)                 # [z, y]

layeron.status("...")
layeron.progress(0.5)
layeron.result("name", value)
layeron.log("...")
layeron.check_cancelled()

layeron.set_volume(array)      # dtype 决定 byte / ushort，整卷原子替换
layeron.mask_create(name, color=None)
layeron.mask_add(name, array, color=None)
layeron.mask_set_slice(handle, z, plane, array)
layeron.mask_publish(handle)
```

实现位于 `Resources/layeron.py`（公开 API）与 `Resources/bootstrap.py`（stdio / REPL 初始化），两者都作为嵌入资源随程序集发布。

---

## 6. 编辑器与文档

`PythonDocuments` 负责：

- 扫描 `<workspace>/python/*.py`，为每个文件注册一个 `DocumentContribution`。
- 新建未命名文档、打开文件、保存/另存为。
- 文件内容可编辑；运行时可选择“保存后运行”或直接运行缓冲区（取决于编辑器动作）。
- 工作区切换时重新列出 `python/` 下的文件。
- 每个文档有自己的工具栏与状态行；没有“当前文档”的全局状态。

REPL 是一个 `ToolContribution`（底部工具面板），不是文档：它不会出现在文档标签栏，也不需要保存。

---

## 7. 设置页与环境

设置窗口的 **Python** 页由插件贡献，包含：

- 解释器 / NumPy / pip 状态。
- 初始化 pip、升级 pip。
- 包输入框、安装、卸载、取消。
- scikit-image 预设。
- 已安装包列表（`pip list --format=json`）。
- 实时 pip 输出。

pip 操作全部是**显式用户动作**，不在启动或打开 REPL 的隐式路径上。pip 安装使用 `PIP_CONSTRAINT` 固定 `numpy==1.23.5`，避免 pip 解析出与已部署 NumPy ABI 不一致的版本。

---

## 8. 发布的服务

插件向扩展点 `layeron.pythonrunner.service` 发布 `IPythonRunnerService`：

```csharp
public interface IPythonRunnerService
{
    const string PointId = "layeron.pythonrunner.service";
    Task<PythonRun> ExecuteAsync(...);
    Task<PythonRun> RunFileAsync(...);
    Task<string?> GetVersionAsync(...);
    Task<IReadOnlyList<PythonPackage>> GetPackagesAsync(...);
    Task<bool> InstallPackagesAsync(...);
}
```

- 该点由插件自己拥有，插件卸载时点与贡献一起消失。
- 消费方（当前是 `Layeron.Plugins.Agent`）通过 `GetPoint<IPythonRunnerService>(IPythonRunnerService.PointId)` 解析，必须引用共享契约程序集 `Layeron.Plugins.PythonRunner.Api`，且 `Private="false"`。
- 解析结果为 `null` 表示“Python 运行器未加载”，调用方应降级而不是崩溃。

---

## 9. 已知限制

1. **解释器只能初始化一次**：pythonnet 在 .NET 10 下不能安全地 Shutdown/Reinitialize，因此插件是应用级生命周期。`ResetSessionAsync` 只重置命名空间，不重启解释器。
2. **取消是协作式的**：`check_cancelled()` 读取宿主标志；纯 CPU 死循环不调用它就无法停止。`InterruptAsync` 只是 best-effort。
3. **pip 需要网络**：首次初始化 pip 会下载 `get-pip.py`；无网络时明确失败，不影响 REPL / NumPy / 已有脚本。
4. **数据 API 是拷贝语义**：脚本拿到的是 NumPy 数组副本，不是主卷视图；写回必须走 `set_volume` / mask API。
5. **工作区切换不重置解释器**：只更新 `sys.path` 与文档列表；旧会话里的变量仍然存在，直到用户执行“重置会话”。
6. **没有 Python 插件格式**：`.py` 不会被 `IPluginSource` 安装为插件；这是与 C# `.csx` 的刻意差异。
