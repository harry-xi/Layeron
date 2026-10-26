# ONNX 模型运行器插件

> 状态：已实现（`Layeron.Plugins.Onnx.Api` / `Layeron.Plugins.OnnxRuntime`）
> 目标：把 ONNX 作为一类"模型运行方式"接进宿主，让模型像体数据过滤器一样出现在用户面前，同时让别的插件能在不引用 ONNX Runtime 的前提下调用推理。

---

## 1. 为什么是插件，而不是宿主功能

ONNX Runtime 是一个体积可观的**原生**运行时（本机 win-x64 一份 `onnxruntime.dll` 约 17 MB，加上托管层约 21 MB）。放进宿主意味着每个用户都为"可能永远不会跑模型"付出这份体积，而且宿主的每个平台都要背着它。

放进插件则三点都成立：

| 目标 | 落地方式 |
|---|---|
| 按需安装 | 插件目录本身就是可选的；不装就没有解析器 |
| 可卸载 | ORT 不需要像 CPython 解释器那样活到进程结束，所以插件**能**被热重载并通过泄漏自检 |
| 契约可共享 | 通过 `plugins.shared` 里的契约程序集，其他插件照样能用推理 |

这与 `Layeron.Plugins.PythonRunner` 的取舍不同，而且差异是刻意的：解释器无法卸载（pythonnet 一旦初始化就不可回收），所以 Python 插件声明为 app 生命周期；ORT 可以卸载，所以这个插件不声明。

---

## 2. 项目与依赖方向

```
Layeron.Plugins.Onnx.Api      契约：IOnnxRunnerService / OnnxModelInfo / OnnxTensorSpec / 请求与结果
        ↑ Private="false"，由 Layeron.App 部署到 plugins.shared
Layeron.Plugins.OnnxRuntime   实现：模型商店 + 张量绑定 + 会话门面 + 面板 + 设置页
        ↑ 随 App 构建，复制到 plugins.dev/layeron.onnxruntime
Layeron.App                    ReferenceOutputAssembly="false" + DeploySamplePlugins / DeploySharedContracts
```

**契约程序集里不出现任何 `Microsoft.ML.OnnxRuntime` 类型。** 这条规则是承重的，不是风格：两份同名的 ORT 类型是两个不同的类型，正如两份 `IPlugin`。一旦契约泄漏 `InferenceSession`，第二个插件解析到的服务就永远转换失败，而且不会报错——查找只是"什么也没找到"。整个插件里 ORT 类型只在一个文件里出现（`OnnxSessionFunctor`），规矩因此由构造保证，而不是靠每次调用时记得。

插件对契约用 `Private="false"`，Avalonia 用 `ExcludeAssets="runtime"`；插件的私有依赖（ORT 与 `System.Numerics.Tensors`）随构建输出落到插件目录，由插件自己的 `.deps.json` 解析——和 Python 插件处理 pythonnet 的方式一致。判定的依据是**进程实际携带的程序集集合**（`HostCarriedAssemblies`）：App 不引用 ORT 与 `System.Numerics.Tensors`，它们就归插件；名字像不像框架程序集不参与判断。

---

## 3. 分层

```
OnnxRunnerPlugin (IPlugin)
  ├─ OnnxSettings      持久配置：模型目录、每个模型的绑定
  ├─ OnnxModelStore    扫描 / 加载 / 丢弃 / 记录失败；一个模型 = 一个可租借的会话
  ├─ OnnxRunnerService 已发布的服务：切片、整卷、原始张量
  │    └─ OnnxInferenceQueue   串行通道 + 线程池跳跃
  └─ OnnxToolView      面板（XAML + ViewModel）
```

拆分的依据是**代价不同**：加载一个模型要创建原生会话并读文件，跑一次推理是热路径。分开之后，重新扫描不必排在已经在跑的推理后面。

### 3.1 `OnnxSessionFunctor`：唯一的 ORT 边界

它只暴露一件事："给我一个 float 张量，还你一个 float 张量"。名字叫 functor 而不叫 Session 是刻意的——它跑一次查询，完全不知道模型意味着什么，所以它上面那层（分块、缩放）可以脱离原生库被测试。

它只持有会话，没有可变状态，所以模型文件变了就整个换掉，而不是原地修改；这也正是**推理路径不需要锁**的原因。

### 3.2 `OnnxModelEntry`：引用计数的会话

模型文件变了必须立刻丢掉会话，但**已经在跑**的推理必须跑完，而在活着的调用底下释放原生会话是崩溃而不是报错。所以会话按租借计数：商店交出自己的那一份，真正的释放由最后一个查询返回时发生。与重新扫描抢跑的调用者拿不到租借，于是得到"模型正在被替换"而不是去碰一个死会话。

### 3.3 `OnnxInferenceQueue`：串行通道

刻意不用每处 `Task.Run`。单通道让一次长整卷推理不会被用户在此期间连续请求的短预览插队，而这个形状正是 Python 插件实测之后定下来的（见 `python-engine-plan.md` §0.3.1）。线程池那一跳是给 UI 用的：没有它，`await` 的第一段同步代码会在调用方线程上跑，窗口就会卡住恰好那么久。

关闭时 `Dispose` 会**通知并 Join** 通道线程。这不是整洁问题：一个活着的线程持着它的委托，因而让整个插件仍可从被 root 的线程对象到达，宿主的泄漏自检就会报出这个插件。有界等待（5 秒）意味着异常长的推理会延后卸载而不是挂住卸载，而且它是后台线程，超时也不会拖住进程。

---

## 4. 绑定：由模型的签名决定，不由用户选模式

这是设计里唯一真正需要判断的地方。用户拿到手的是模型，不是"模式"——所以**签名的轴数决定绑定方式**：

| 输入签名 | 输入来源 | 产出 |
|---|---|---|
| `[N,C,H,W]`（H/W 固定） | 分块读取当前切片 | 概率/结构图 → 预览叠加 → 掩膜层 |
| `[N,C,H,W]`（H/W 开放） | 整张切片一次读完 | 同上 |
| `[N,C,D,H,W]` | 分块读取整卷 | 新体数据 → `IVolumeService.Replace` |

`OnnxSpatialExtent` 表达这件事：零表示"这个轴是开放的"，正数表示"模型要求固定尺寸"。零与固定是**不同**的答案，各自干活：开放轴意味着一次读取整块区域，固定尺寸才是让整卷运行分块的原因。

### 4.1 分块的重叠与归属

相邻分块的**读取**窗口重叠两倍的 halo，而每块只写自己的**内部**——距离读边界至少一个 halo 体素的部分。这让结果精确而不是"看起来还行"：被写入的每个体素都是用一个周围带完整真实数据的窗口算出来的，只在区域自身的边界处例外，而那里的边缘就是真实边缘。

分块只在模型开放的那些轴上发生。一个固定三个空间轴的模型按自己的分块尺寸推进，因而能用在任意大小的体数据上；一个固定高宽但不固定深度的模型按平面推进，因为 2D 模型不该为每层付一次 3D 调用的开销。

### 4.2 阈值是范围的比例，不是绝对值

`OnnxBinding.ResolveThreshold` 从输出范围的**中点**解析阈值——带状滤波模型把平坦内容放在中点。于是"0.6"对每个模型都意味着"比平坦高出这么多"，而不是每个模型各有一层含义。预览与应用走同一个解析结果是刻意的：**预览是对应用结果的承诺**，这是 `ThresholdEngine` 立下的规矩，这里照做。

---

## 5. 已发布的服务

```csharp
public interface IOnnxRunnerService
{
    const string PointId = "layeron.onnx.runner";
    IReadOnlyList<OnnxModelInfo> Models { get; }
    event Action? ModelsChanged;
    Task<int> RescanAsync(CancellationToken ct = default);
    OnnxModelInfo? GetModel(string idOrNameOrPath);
    Task<OnnxSliceResult> RunSliceAsync(OnnxSliceRequest request, CancellationToken ct = default);
    Task<OnnxVolumeResult> RunVolumeAsync(OnnxVolumeRequest request, CancellationToken ct = default);
    Task<OnnxRunResult> RunAsync(OnnxRunRequest request, CancellationToken ct = default);
}
```

消费方按 id 解析，**不需要**引用本插件。两点必须知道：

1. **点是插件自己拥有的**，所以本插件卸载时点也消失。消费方必须把"解析结果为 null"当作"运行器未加载"，而不是期待一个空点。
2. **必须用共享契约程序集**（`Private="false"`），否则两边的 `IOnnxRunnerService` 名字相同却类型不同，服务取到也转不过去，且不会报错。详见 `docs/plugin/extension-points.md`。

原始张量运行**只接受一个输入**。第二个输入通常携带只有调用方才知道含义的参数，与其假装支持，不如带原因拒绝一次调用——半跑的静默结果要糟得多。

---

## 6. 演示模型

两个模型都是同一个图像处理流程（反锐化掩模）表达成 CNN 实际会用的算子图，所以运行器是在真实计算图上被检验，而不是直通：

```
s = AveragePool(x, k)          低通：局部背景
b = x - s                      带通：细节结构
d = Conv(b, K)                 边缘增强（3×3 / 3×3×3 反锐化核）
y = Clip(0.5 + d)              可视化输出 [0,1]，平坦区域落在 0.5
```

| 文件 | 签名 | 用途 |
|---|---|---|
| `demo_structure_slice.onnx` | `[1,1,?,?] → [1,1,?,?]`，757 B | 演示任意尺寸切片、单次推理 |
| `demo_structure_volume.onnx` | `[1,1,16,64,64] → 同形`，942 B | 演示固定尺寸模型被分块驱动 |

生成脚本用 `onnx` 程序化构造并 `onnx.checker` 校验；`ir_version` 固定为 10，因为 onnx 1.23 默认的 IR 14 会被 ORT 1.31 拒绝加载（实测）。增益取 1.0：在阶跃圆盘上，平坦背景精确停在 0.5，圆盘边缘触及两端，而一个软渐变带保留自己的中间值而不是饱和。

### 为什么是一个自制的"结构增强"模型

- **完全离线**：不需要下载、没有许可问题、没有网络依赖。
- **确定性**：同样的输入必然同样的输出，测试可以直接断言数值。
- **对体数据真的有结果**：直接演示整卷推理并替换会话数据这一整条链路。
- **子千字节**：作为嵌入资源随程序集发布，不污染仓库。

模型作为 `EmbeddedResource` 内嵌，**直接从内存加载**（`InferenceSession(byte[], SessionOptions)`）。不落盘意味着没有部署这一步、没有"用户删掉又装回来"的语义要拿捏、没有需要维护的模型目录，也不需要为磁盘上没有文件的情况编造一条失败原因。

---

## 7. 线程模型

| 环节 | 线程 |
|---|---|
| 面板状态、预览发布 | UI 线程 |
| 模型扫描、会话创建、推理、掩膜位图构建 | ONNX 通道（后台线程） |
| 面板等待 | `await`，续体显式回到 `Dispatcher`，不依赖同步上下文 |

面板里的运行是**被取代**而不是排队：移动十字线或拖阈值会启动新预览并取消上一次。这是滑杆在这么大一块数据上还跟得上手的原因。

---

## 8. 性能实测

| 场景 | 耗时 |
|---|---|
| 切片模型，64×64 | 0.5 ms |
| 切片模型，200×200 | 0.6 ms |
| 整卷模型，16×64×64 分块 | 1.3 ms |
| 256×256×128 整卷（8 块） | 约 10 ms 推理 + IO |

CPU provider，`InterOpNumThreads = 1`。单线程是刻意的：运行器本来就用分块并行，让每个会话再起一个池只会更慢。

---

## 9. 已知限制

- **只有 CPU。** 选项里没有 provider 选择器：GPU 后端是另装包、另带原生库的部署决定，运行器不替用户做。面板会显示会话**实际**采用的 provider（ORT 1.31 没有扁平的"已分配 provider"访问器，但绑到输入上的设备回答了同一个问题）。
- **原始张量运行只绑一个输入。**
- **整卷输出的范围来自绑定，不是测量得出。** 测量意味着再跑一遍整卷，或者物化一份全数据的浮点体数据——两者都远差于让用户填面板上已经显示的两个数字。填充值因此是"把整个信号缩放到元素类型的量程"，而不是裁剪。
- **每次加载只跑一个模型。** 模型链、批处理、多模型对比都不在范围内。
- **没有模型元数据缓存。** 每次启动重新读签名；对一份模型目录来说便宜到可以忽略。

---

## 10. 测试

`Layeron.Plugin.Test/OnnxRunnerTests.cs` 走真实演示模型，因此内嵌资源读取、会话创建、**原生 ORT 解析**、分块、缝合与量化全都被覆盖，不需要磁盘上有文件，也不需要维护夹具。

覆盖点：内嵌资源的读取与体量；签名读取与动态维度；三种平面的体素映射；分块恰好一次划分且保持 halo；切片路径的"平坦区域停在中点、边缘被强调"；固定尺寸模型的分块缝合；整卷分块与中心点写入；元素类型分派；模型不存在与无体数据时的拒绝；设置的往返与越界修正；插件加载、贡献、卸载与泄漏自检。

两处细节值得记下来，因为它们是踩过的坑：

- **Debug 构建下泄漏检查必须在加载帧返回之后。** JIT 会把局部变量保活到方法结束，所以在同一帧里做检查会报出测试**自己**造成的泄漏。
- **测试不能跨加载上下文做类型化查找。** 宿主从 `plugins.shared` 加载契约，与测试的默认上下文是两个上下文，所以类型化的 `GetPoint<IOnnxRunnerService>` 比较的是两个仅同名的类型，必然找不到——测试因而只断言点的**字符串 id**。

还有一处是**产品缺陷**，不是测试问题，值得单独记下来修养列表：

- **重建 `ObservableCollection` 必须串行化。** 面板的模型列表是"清空再填充"，而触发它的路径有三条同时存在的：构造函数里那次扫描、用户点扫描、以及 store 的 `Changed` 事件（由后台线程加载完成时触发）。它们并发进入时，两次 `Clear` 会与彼此的 `Add` 交错，**同一个模型会在列表里出现两次**。表现是随机的列表重复。修法是把重建整体放进一把锁，并让 store 事件也走面板统一的 UI 编组缝隙（而不是直接碰 `Dispatcher`），否则这条路径在无头测试里根本跑不到。
