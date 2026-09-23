# Layeron 插件系统

> 状态：已实现（Layeron.Plugin.Abstractions / Layeron.Plugin.Host / Layeron.Plugins.Threshold / Layeron.Plugins.ScriptHost）
> 本文取代 `EXTENSION_ARCHITECTURE.md` §4–§5 的插件与内核设想，为插件相关设计的唯一权威。

---

## 1. 目标

| 目标 | 落地方式 |
|---|---|
| 插件能加**几乎一切**功能 | 工具面板 / 菜单 / 扩展点（插件可自定义新扩展点） |
| **热加载 / 卸载** 无残留 | 一个插件 = 一个 collectible ALC + 一个可逆作用域 |
| 插件代码零散落进宿主 | 宿主只持有 `object` 与接口，从不 `as` 插件类型 |
| 卸载可**验证** | 弱引用自检，泄漏会报出插件名 |
| 插件自己也能是**插件加载器** | `IPluginSource`：发现即子作用域，卸载父即卸载子 |
| 大体积数据不翻倍 | 块级读写 API，插件遍历 `ReadOnlySpan<T>` 而非逐体素索引器 |

**反面目标（刻意不做）**：不做 `IVolume` 之类的中间抽象层；不做 `manifest.json` 描述 .NET 插件（元数据已在程序集里）；不做"唯一世界文件"；不为了兼容旧格式加抽象。

---

## 2. 项目与依赖方向

```
Layeron.Big3DArray        卷 / 位图核心类型 + 块级读写 API（零依赖）
Layeron.Plugin.Abstractions  插件契约（只依赖 Big3DArray，不含任何 UI 框架）
Layeron.Plugin.Host      加载器内核：PluginManager / PluginLoadContext / 作用域 / 扩展点注册表
Layeron.Plugins.Threshold    示例插件：阈值分割工具（UI 插件）
Layeron.Plugins.ScriptHost   示例插件：本身是插件加载器（见 §10，未完成）
Layeron.App              Avalonia 宿主：实现 IPluginHostAdapter，把贡献接到 Dock 与菜单
Layeron.Plugin.Test      插件系统测试（无 UI）
```

依赖方向：`Abstractions → Big3DArray`；`Host → Abstractions`；`App → Host`；**插件 → Abstractions + Big3DArray（仅编译期）**。

关键点：插件项目对 `Layeron.Big3DArray` / `Layeron.Plugin.Abstractions` 使用 `Private="false"`，Avalonia 使用 `ExcludeAssets="runtime"`。运行时这些程序集一律来自宿主，否则插件会看到第二份 `IPlugin`，出现"类型对不上"的静默失败。

---

## 3. 三个概念

内核只有三个概念，其余都是它们的组合：

1. **`PluginContext`（作用域）** —— 一个插件的生命周期。所有副作用登记在这里，`Dispose` 逆序回滚；子作用域递归释放。
2. **`IPluginRegistry`（贡献面）** —— 插件能"加"的一切：工具、菜单、扩展点、插件源、任意 `IDisposable`。**每个注册都自动绑定到所属作用域。**
3. **`ExtensionPoint<T>`（插槽）** —— 插件可以定义新插槽，别的插件往上挂；`T` 是宿主标准类型，因此贡献方无需引用定义方的程序集。

```
IPlugin.Activate(ctx)
  ├─ ctx.Registry.AddTool(...)        → 作用域登记 → Dispose 时卸载
  ├─ ctx.Registry.AddMenu(...)
  ├─ ctx.Registry.DefinePoint<T>(id)  → 点随该插件卸载而消失
  ├─ ctx.Registry.Contribute(id, val) → 贡献随该插件卸载而移除，即使插槽属于别人
  ├─ ctx.Registry.AddPluginSource(...) → 本插件成为加载器（§8）
  └─ ctx.Services.{Volumes,Masks,Preview,Log}
```

**没有 `Shutdown`**：需要记住该清理什么，早晚会漏。只有可逆上下文。

---

## 4. 插件身份：元数据在程序集里

.NET 插件把身份写进程序集元数据（不是 class 特性，也不是外部清单）：

```xml
<ItemGroup>
  <AssemblyMetadata Include="LayeronPluginId"          Value="layeron.threshold" />
  <AssemblyMetadata Include="LayeronPluginName"        Value="Threshold Segmentation" />
  <AssemblyMetadata Include="LayeronPluginVersion"     Value="1.0.0" />
  <AssemblyMetadata Include="LayeronPluginDescription" Value="..." />
  <AssemblyMetadata Include="LayeronPluginApiVersion"  Value="1.0" />
</ItemGroup>
```

宿主用 `MetadataLoadContext` **只读**这些行：不执行模块初始化器、不跑静态构造、不需要能解析插件依赖，坏 DLL / 敌意 DLL 在运行前就被拒。

- 没有元数据的 DLL → 直接跳过（DEBUG 日志）。这条规则让插件目录里的私有依赖（Roslyn、引用程序集）**不会被加载**，既快又安全：
  ```
  [Plugin:DBG] Skipping 'Microsoft.CodeAnalysis.CSharp.dll': no plugin metadata.
  ```
- `[Plugin("id")]` 特性仍然标注入口类（`PluginManager` 用它找 `IPlugin` 实现），但 id / 版本以元数据为准，不一致会记日志。
- **Python / 脚本等非 .NET 插件没有程序集元数据**，由承载它们的脚本宿主插件读自己的 `manifest.json`，再转成同一个 `PluginCandidate`（§10）。宿主只认 `PluginCandidate`，不关心来源格式。
- API 版本主版本不匹配 → 拒绝加载并给出明确警告。

---

## 5. 加载、卸载与泄漏验证

```csharp
var adapter = new AppPluginHostAdapter(dock, services, log);       // 宿主侧
var manager = new PluginManager(adapter, new PluginManagerOptions());
manager.LoadAll();                                                 // plugins.dev 优先，然后 plugins
manager.Reload("layeron.threshold");                               // 热重载
manager.Unload("layeron.threshold");                               // 卸载（含它加载的子插件）
manager.VerifyUnloaded();                                          // 返回仍然活着的插件 id
```

**目录布局**（两种都支持）：

```
plugins.dev/                     开发目录（默认在 exe 旁，先扫描，同名覆盖已安装版本）
  layeron.threshold/Layeron.Plugins.Threshold.dll + .deps.json
  layeron.scripthost/...          + 私有依赖（Roslyn）
  some-drop-in.dll               单文件平铺也支持
%LocalAppData%/Layeron/plugins/  正式安装目录
```

一个插件一个目录，是因为带私有依赖的插件必须让 `.deps.json` 与其程序集在一起，`AssemblyDependencyResolver` 才能解析。

**卸载顺序**（`PluginManager.Unload`）：

1. 收集该插件及其**所有嵌套子插件**（按作用域父子链）；
2. 释放根作用域 → 子作用域先被释放 → 各作用域内贡献逆序回滚（工具/菜单/扩展点贡献/事件订阅）→ 清理该插件发布的预览覆盖层；
3. 各插��的 `AssemblyLoadContext.Unload()`；
4. 丢弃该插件定义的扩展点、其插件源与发现记录；
5. 记录弱引用，`VerifyUnloaded()` 触发 GC 后报告**仍然存活**的加载上下文。

**为什么宿主不被钉住**：`VerifyUnloaded` 只保存 `WeakReference + id`。早期版本保存 `LoadedPlugin` 记录，记录里的 `IPlugin` 实例反过来钉住了 ALC——这正是自检要发现的那类问题，于是自检自己先犯了一次；测试 `ThresholdPlugin_UnloadRemovesContributionsAndCollectsContext` 现在守住这条线。

**Avalonia 是最容易钉住 ALC 的地方**，因此：

- 插件的 View/ViewModel 只在插件 ALC 内创建，宿主只见 `object`（`ToolContribution.ContentFactory` 的返回值）；
- `PluginToolViewModel.Dispose()` 会 `Dispose` 插件内容，插件的面板在 `Dispose` 里解绑宿主服务事件（`ThresholdToolView.Detach`）；
- 关闭面板时释放内容，卸载插件时关闭该插件的所有面板（`DockFactory.CloseDockables`）。

---

## 6. 宿主服务（插件能看到的数据）

`IPluginServices` 是插件访问应用状态的**唯一**通道，全部是对既有会话对象的适配，不存在第二套数据模型。

```csharp
IVolumeService Volumes   // Current(ReadonlyBig3DArray) · Version · Replaced · CurrentX/Y/Z · CrosshairChanged
                         // · ComputeHistogram(ct, progress) -> long[]?
IMaskService   Masks     // Layers · Add(name, Big3DBitMap, MaskColor) · Remove · NotifyContentChanged · Changed
IPreviewChannel Preview  // Publish(PreviewOverlay) · Clear(ownerId) · Overlays · Changed
IPluginLog     Log
```

要点：

- **主卷直接给核心类型，不做包装**。插件拿到 `ReadonlyBig3DArray`（非泛型基类），自己 pattern match 一次 `is ReadonlyBig3DArray<byte> / <ushort>`，然后完全用核心类型工作：无门面、无反射分发、无逐体素装箱。宿主的 `SliceViewerViewModel.ExtractScalarSlice` 就是这么写的，插件照抄即可。
- **`Version` 存在的原因**：主卷是原子替换的。后台算完的结果如果 `version != Volumes.Version` 就丢弃，避免把过期掩膜交给用户；插件不需要额外锁。
- **整卷统计由宿主提供，插件不重复实现**：`Volumes.ComputeHistogram(ct, progress)` 返回 `VolumeHistogram.BinCount` 个 bin（8-bit 一值一 bin，16-bit 256 值一 bin，覆盖元素类型全值域），可后台线程调用、可取消、按块报进度。宿主自己的窗宽窗位直方图与插件用的是**同一份实现**（核心 `Layeron.Big3DArray.VolumeHistogram`），所以两边永远不���对同一卷给出不同的统计；插件只负责画图。
- **掩膜是用户数据，预览是插件状态**。掩膜（分割结果）在插件卸载后保留；预览覆盖层随插件卸载自动清空——所以卸载后不会留下幽灵叠加层。
- `NotifyContentChanged()`：插件用块 API 整块写掩膜后主动通知。块级 API 故意不发逐体素事件。

### 块级读写 API（高性能遍历）

```csharp
// 卷（ReadonlyBig3DArray<T>）
int BlockCount;  int GetBlockIndex(x,y,z);  (int,int,int) GetBlockOrigin(blockIndex);
ReadOnlySpan<T> GetBlockSpan(int blockIndex);          // 零拷贝视图
ReadOnlySpan<T> GetBlockSpan(bx,by,bz);  void CopyBlock(int, Span<T>);
bool IsBlockFullyInside(int blockIndex);              // 整块都在卷内？
void ForEachInVolumeRange(int blockIndex, Action<int,int> range);  // 只遍历卷内体素

// 位图（Big3DBitMap）
void SetBlock(bx,by,bz, ReadOnlySpan<bool> values);    // 整块提交，自动复用空/满块
void ClearAllBlocks();
```

- 块布局固定：块内 `z-y-x`，即 `lz*64*64 + ly*64 + lx`；`GetBlockOrigin` 给出块的世界原点。
- **必须用 `ForEachInVolumeRange` 而不是裸 `span` 遍历**：存储永远按 64³ 整块分配，尺寸不是 64 倍数的卷，边缘块里含有**卷外**的填充���素，那是不含数据的未初始化内存。该 API 在块完全在卷内时给出"整块一个区间"（常见情形零开销），边缘块才逐行给区间——把这条正确性规则收在核心一处，插件不必各自踩坑。裸 `GetBlockSpan` 只在 `IsBlockFullyInside` 成立（或填充确实无关）时才正确。
- 核心已经把整卷统计按这条规则写好了（`VolumeHistogram`），插件要直方图就走 `Volumes.ComputeHistogram`，不要自己再写一遍块遍历。
- **不做宽化、不做整卷拷贝**：512³ 8-bit 卷阈值分割只用 ~256 KB 临时缓冲；把 8-bit 拉平成 16-bit 会让 128 MB 变 256 MB，明确禁止。

---

## 7. 扩展点与单向依赖

`DefinePoint<T>` / `Contribute` / `GetPoint<T>` 让"插件扩展插件"成为一等公民：

```csharp
// 阈值插件发布插槽（T 是宿主标准类型 VolumeAction）
var point = context.Registry.DefinePoint<VolumeAction>("layeron.threshold.actions", "Threshold panel extensions");

// 任意其他插件 / 脚本贡献按钮，无需引用阈值插件程序集
context.Registry.Contribute("layeron.threshold.actions", new VolumeAction {
    Header = "肺窗预设", Execute = services => services.Log.Info("…")
});
```

**类型归属规则（重要）**：扩展点的���献类型必须是**宿主标准类型**（`Layeron.Plugin.Abstractions` 中的类型，如 `VolumeAction`、`MenuContribution`）。如果定义方用自己的类型，贡献方就得引用它在运行时的那份拷贝，而每个插件各有一份拷贝，类型不再相等 → 静默失败。这是程序集隔离的必然约束，也是 `VolumeAction` 放在 Abstractions 的原因。

消费方订阅 `point.Added/Removed` 保持同步，并把订阅登记到作用域（或在自己的 `Dispose` 里解绑，如 `ThresholdToolView`）。

---

## 8. 插件作为插件加载器

```csharp
public interface IPluginSource {
    string Id { get; }
    IEnumerable<PluginCandidate> Discover(IPluginSourceContext context);
}

// 在插件里：
context.Registry.AddPluginSource(new MySource());
```

- 该源发现的候选一律作为**该插件的子作用域**激活 → 卸载加载器即卸载它加载的一切（任意深度），无需额外记账。
- 候选分两种：`Instance`（进程内，由源自己创建，如脚本）与 `Location`（托管程序集，宿主放进新的 collectible ALC）。
- `PluginManager.ReloadSource(sourceId)` = 卸载该源提供的插件 → 清发现记录 → 重新发现并激活。这就是"脚本改完点一下重载"。
- 宿主因此**不需要知道任何插件格式**。Python 宿主、F# 宿主、远程源都可以这样加进来。

`App` 侧对应能力：插件菜单里的"重新扫描目录"会 `LoadAll()` + `RefreshSources()`。

---

## 9. 参考实现：阈值分割插件

`Layeron.Plugins.Threshold` 是完整样例，它演示了本设计的每一条：

| 关注点 | 做法 |
|---|---|
| 身份 | csproj 的 `AssemblyMetadata`（§4） |
| 贡献 | `AddTool`（面板）+ `AddMenu`（菜单）+ `DefinePoint<VolumeAction>`（被别人扩展） |
| 泛型去重 | 每操作只 `switch` 一次 byte/ushort，其余全是单个泛型方法（`where T : unmanaged, INumber<T>`） |
| 性能 | 只用块 API；每块一个池化 `bool[64³]` 缓冲；`Big3DBitMap.SetBlock` 整块提交；掩膜单遍 |
| 边界正确性 | 用核心的 `ForEachInVolumeRange` 只取卷内体素——尺寸非 64 倍数也精确（这条规则收在核心，插件不再各自踩坑） |
| 交互 | 阈值滑块 + 对数直方图（数据来自 `Volumes.ComputeHistogram`，自绘 `Control` 只负责呈现）+ 预览开关 + 进度/取消 |
| 预览 | `Preview.Publish`（XY/XZ/YZ 三个当前层同时发布），切片视图各自渲染为独立叠加层 |
| 结果 | `Masks.Add` 生成普通掩膜层，之后与手动掩膜完全同权 |
| 卸载 | `ThresholdToolView.Dispose` 解绑宿主事件并清预览 |
| 不碰 UI 类型也可激活 | 面板由宿主 `ContentFactory` 惰性创建，`Activate` 里不出现任何 Avalonia 类型 → 无 UI 宿主（测试/批处理）也能加载 |

测试覆盖：byte/ushort 全卷掩膜与暴力逐体素结果逐点比对、三种切片平面、边界卷（70×68×66）、阈值反向自动排序、越界钳制、进度单调、取消、以及生命周期/嵌套/泄漏（§11）。直方图现在由核心 `VolumeHistogram` 提供，其准确性（含边缘块填充不计入）在 `Layeron.Big3DArray.Test` 覆盖。

---

## 10. 脚本宿主（未完成）

`Layeron.Plugins.ScriptHost` 已实现为**普通插件 + 插件源**：扫描 `plugins*/scripts/*.csx`，每个脚本包成进程内 `IPlugin`，脚本里可 `Menu(...)` / `Tool(...)` / `Contribute(...)`。

当前阻塞点（诚实记录）：Roslyn 把脚本的 **globals 类型**加载进它自己的 `CoreAssemblyLoaderImpl` 加载上下文，于是 `Layeron.Plugins.ScriptHost` 出现第二份拷贝，运行时 `ScriptGlobals` 强转失败：

```
InvalidCastException: [A]Layeron.Plugins.ScriptHost.ScriptGlobals cannot be cast to
[B]Layeron.Plugins.ScriptHost.ScriptGlobals
```

修法（不是打补丁，是设计决策）：把脚本面向的 API 移到**只加载一份**的程序集里，globals 类型只用那一份类型；脚本便利方法可作为该程序集上的静态方法/扩展方法，只接受宿主标准类型参数。选定后再实现 Python 宿主（读 `manifest.json` → `PluginCandidate`）。

`Layeron.Plugin.Test/ScriptHostTests.cs` 已写好全部场景（加载、编辑后重载、跨插件扩展、坏脚本隔离），当前 `[Ignore]`，修好后删掉该标记即可。

---

## 11. 测试策略

无 UI 也能测，是本设计的直接收益：`Layeron.Plugin.Test` 用一个 `TestHostAdapter` 实现与 App 相同的 `IPluginHostAdapter`。

| 层次 | 覆盖 |
|---|---|
| 核心块 API | 块跨度与索引器一致性、零拷贝视图、`ForEachInVolumeRange` 覆盖集合与卷内体素完全一致（填充不被访问）、内部块单区间、`SetBlock` 往返、空/满块复用、缓冲区长度校验；`VolumeHistogram` 与暴力计数逐 bin 比对（byte/ushort、边界卷与精确块倍数、bin 划分、进度、取消、空卷） |
| 插件生命周期 | 加载/卸载/重复 id/激活抛异常回滚/管理器 Dispose |
| 作用域 | 定义者卸载 → 扩展点消失；贡献者卸载 → 贡献消失而插槽仍在；预览被清理；掩膜被保留 |
| 嵌套加载 | 子插件归属、卸载加载器级联卸载、只卸载子插件、源重载不产生重复 |
| 真实程序集 | 元数据身份、加载、卸载后 ALC 被回收（弱引用自检）、热重载得到新实例、非插件程序集静默跳过 |
| 算法 | 阈值结果与暴力实现逐体素比对（byte/ushort/三平面/边界卷）、进度、取消 |

真实宿主（Avalonia）不需要自动化也能确认加载链路，运行一次 app 看日志即可：

```
[Plugin:INF] Loaded plugin 'layeron.scripthost' (v1.0.0).
[Plugin:INF] Loaded plugin 'layeron.threshold' (v1.0.0).
```

---

## 12. 编写一个插件

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies> <!-- 有私有依赖时需要 -->
  </PropertyGroup>
  <ItemGroup>
    <AssemblyMetadata Include="LayeronPluginId" Value="me.myplugin" />
    <AssemblyMetadata Include="LayeronPluginApiVersion" Value="1.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Layeron.Big3DArray\Layeron.Big3DArray.csproj" Private="false" />
    <ProjectReference Include="..\Layeron.Plugin.Abstractions\Layeron.Plugin.Abstractions.csproj" Private="false" />
  </ItemGroup>
</Project>
```

```csharp
[Plugin("me.myplugin", Name = "My Plugin", Version = "1.0.0")]
public sealed class MyPlugin : IPlugin
{
    public void Activate(IPluginContext context)
    {
        context.Registry.AddMenu(new MenuContribution {
            Path = "插件/我的功能", Header = "统计体素数",
            Execute = () => {
                var volume = context.Services.Volumes.Current;
                if (volume is null) return;

                // 泛型方法只写一遍，byte / ushort 共用；派发只发生这一次。
                var total = volume switch
                {
                    ReadonlyBig3DArray<byte> v8 => Count(v8),
                    ReadonlyBig3DArray<ushort> v16 => Count(v16),
                    _ => 0L
                };

                context.Services.Log.Info($"voxels = {total}");
            }
        });
    }

    private static long Count<T>(ReadonlyBig3DArray<T> volume) where T : unmanaged, INumber<T>
    {
        long total = 0;

        for (var block = 0; block < volume.BlockCount; block++)
        {
            // ForEachInVolumeRange 跳过边缘块的填充体素；块完全在卷内时它给出整块一个区间。
            volume.ForEachInVolumeRange(block, (offset, count) =>
            {
                var span = volume.GetBlockSpan(block);
                var end = offset + count;

                for (var i = offset; i < end; i++)
                    total += long.CreateChecked(span[i]);
            });
        }

        return total;
    }
}
```

> 遍历块时**一定**用 `ForEachInVolumeRange`：存储按 64³ 整块分配，尺寸不是 64 倍数的卷，边缘块里含卷外的填充体素。
> 只有 `IsBlockFullyInside(block)` 为真时，直接读整段 `GetBlockSpan` 才与卷内容等价；阈值/直方图这类精确操作若自行读整段，会把填充算进结果。

放到 `plugins/<id>/` 或 `plugins.dev/<id>/`（后者在 exe 旁，开发时首选），重启或"重新扫描目录"即可。

---

## 14. 体滤波器（已实现契约，未实现驱动）

设计取舍：**核心定域，插件只算映射**。源卷常驻内存（1024³ 16-bit 即 2 GiB），预览读邻域不需要裁剪拷贝，只需少算输出——因此预览与应用是同一个函数，区别只在核心传入的 `FilterRegion`（预览 = 可见片的厚 1 盒，应用 = 全卷盒）。没有 halo 元数据：插件需要邻域就直接读 `src` 的任意坐标，谎报即无从谈起；没有切片特化函数：一份 `Run<T>` 同时服务预览与应用。

```csharp
public interface IVolumeFilter
{
    string Id { get; }
    string DisplayName { get; }
    string? Description { get; }
    void Run<T>(ReadonlyBig3DArray<T> src, Big3DArray<T> dst, FilterRegion region, FilterContext ctx)
        where T : unmanaged, INumber<T>;
}

// 注册：落在宿主拥有的 layeron.filters.volume 点上
context.Registry.AddFilter(new Mean3x3Filter());

// 实现骨架：只写 region，只读 src，邻域越界一律钳制到边缘
public void Run<T>(ReadonlyBig3DArray<T> src, Big3DArray<T> dst, FilterRegion region, FilterContext ctx)
    where T : unmanaged, INumber<T>
{
    for (var z = region.Z0; z < region.Z0 + region.Z; z++)
    for (var y = region.Y0; y < region.Y0 + region.Y; y++)
    for (var x = region.X0; x < region.X0 + region.X; x++)
    {
        ctx.CancellationToken.ThrowIfCancellationRequested();
        // VolumeFilterSampling.GetClamped(src, ...) 读 3×3×3 邻域，均值后写 dst[x, y, z]
    }
}
```

规则（写进契约注释，靠评审与测试执行，而非靠插件自报元数据）：

- **只写 `region` 内体素**，预览盒与全卷盒由核心选择，插件不感知自己在算预览还是应用。
- **不分配卷**：`dst` 由核心按源尺寸/类型预分配；插件 `new` 卷即错。
- **不扫全卷取统计**：全局 min/max 由核心预扫后经 `FilterContext.GlobalMin/GlobalMax` 下发（直方图复用核心 `VolumeHistogram`）；`Run` 内自扫即错——切片预览与全卷应用会在该处分叉。
- **邻域越界钳制到边缘**（`VolumeFilterSampling.GetClamped`），v1 固定此一种策略，无选项。
- **读经块 API 语义**：边缘块填充不是数据；`GetClamped` 走索引器保证正确，热路径优化（块 span 直读）是核心后续工作，不进插件契约。
- **每块查取消、每块报进度**；`region` 恒在卷内，`IsEmpty` 时直接返回。
- **切片与全卷一致**：同一参数下全量结果切一片，必须与切片预览逐点一致（`FilterRegistrationTests.SliceAndFullRun_AgreeOnTheSlice` 守住这条线）。

未实现（诚实记录）：`FilterContext.GlobalMin/GlobalMax` 的预扫驱动、`dst` 分配与 `FilterRegion` 驱动（含预览防抖/取消、`ReplaceVolume` 原子提交与版本过期丢弃）、`SliceViewer` 的预览片渲染分支、驱动侧 `byte/ushort` 一次派发、FFT/多轮迭代类的 ROI 试算降级。`AddFilter` 注册、宿主拥有点、卸载摘除、钳制采样已实现并有测试。

---

## 15. 已知限制

1. **脚本宿主未完成**（§10）。
2. **进程内信任模型**：插件与宿主同进程，不做沙箱。破坏性/恶意插件能做的事等于应用能做的事；v1 依靠"安装即信任 + 加载前元数据校验"。
3. **卸载不是立即的**：`ALC.Unload()` 之后要 GC 才回收；宿主页面/事件若被静态引用仍会泄漏，靠 `VerifyUnloaded` 发现而不是祈祷。
4. **依赖冲突**：插件私有依赖随插件加载；若与宿主同名程序集（Avalonia、ReactiveUI 等）冲突，宿主版本优先——这是刻意的（类型同一性 > 插件自由），也因此插件不能替换宿主 UI 栈。
5. **布局恢复**：插件未加载时其面板在布局里成为"未解析窗口"（`UnresolvedDockable`），不会破坏布局；重新加载插件后重新打开即可。
6. **热重载不迁移状态**：当前实现是卸载→重新加载。若要保留面板/参数状态，需要插件的显式序列化钩子（未实现）。
