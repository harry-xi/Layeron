# Layeron 插件系统

> 状态：已实现（Layeron.Plugin.Abstractions / Layeron.Plugin.Host / Layeron.Plugins.Threshold / Layeron.Plugins.ScriptHost）
> 本文档为插件相关设计的唯一权威。旧版 `EXTENSION_ARCHITECTURE.md` §4–§5（CapabilityDescriptor / RegisterApplyFilter / Sdk+Kernel 设想）已废弃，仅保留其核心数据模型、切片/渲染、持久化章节有效。

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

**常规插件体验目标**：插件开发者在常规状态下，像在调用一个设计良好的数据存储核心（Big3DArray），并通过一套易用的 API 透过 App 与用户交互。面板允许并鼓励直接使用 Avalonia（XAML + ViewModel + 绑定）；手拼 400 行控件代码是示例写法问题，不是契约要求。

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

## 3. 分层：直连接入 vs 扩展点

核心只有三个概念：

1. **`PluginContext`（作用域）** —— 一个插件的生命周期。所有副作用登记在这里，`Dispose` 逆序回滚；子作用域递归释放。
2. **`IPluginRegistry`（贡献面）** —— 核心只留泛型机制：`DefinePoint / GetPoint / Contribute / Track`。宿主功能全部强类型化，但以下沉为 `Abstractions` 扩展方法实现，加法不破 ABI。
3. **`ExtensionPoint<T>`（插槽）** —— 仅用于**多贡献者聚合**（Filter / Settings / 插件自建的 `VolumeAction`）。`T` 是宿主标准类型，因此贡献方无需引用定义方的程序集。

```
IPlugin.Activate(ctx)
  ├─ ctx.Registry.AddTool(...)        → 直连 Adapter（单消费者：Dock），返回 IDisposable
  ├─ ctx.Registry.AddMenu(...)        → 直连 Adapter（单消费者：菜单），返回 IDisposable
  ├─ ctx.Registry.DefinePoint<T>(id)  → 点随该插件卸载而消失
  ├─ ctx.Registry.Contribute(id, val) → 贡献随该插件卸载而移除，即使插槽属于别人
  ├─ ctx.Registry.AddFilter(...)       → 扩展方法 → Contribute(FilterPoints.Volume) + 全局 Id 校验
  ├─ ctx.Registry.AddSettingsPage(...) → 扩展方法 → Validate + Contribute(SettingsPoints.Pages)
  ├─ ctx.Registry.AddPluginSource(...) → 本插件成为加载器（§8），返回 IDisposable
  └─ ctx.Services.{Volumes,Masks,Preview,Log}
```

** Tool/Menu 为什么不是点**：单一消费者（宿主 Dock/菜单），一对一 `Attach → handle.Dispose()` 撤销，无需 `Items` 列表、`Added/Removed` 事件、跨插件读取。做成点只增加排序、事件重放、宿主拥有点生命周期等成本，无收益。

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

- 没有元数据的 DLL → 直接跳过（DEBUG 日志）。
- `[Plugin("id")]` 特性仍然标注入口类（`PluginManager` 用它找 `IPlugin` 实现），但 id / 版本以元数据为准，不一致会记日志。
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

**卸载顺序**（`PluginManager.Unload`）：

1. 收集该插件及其**所有嵌套子插件**（按作用域父子链）；
2. 释放根作用域 → 子作用域先被释放 → 各作用域内贡献逆序回滚 → 清理该插件发布的预览覆盖层；
3. 各插件的 `AssemblyLoadContext.Unload()`；
4. 丢弃该插件定义的扩展点、其插件源与发现记录；
5. 记录弱引用，`VerifyUnloaded()` 触发 GC 后报告**仍然存活**的加载上下文。

**Avalonia 是最容易钉住 ALC 的地方**，因此：

- 插件的 View/ViewModel 只在插件 ALC 内创建，宿主只见 `object`（`ToolContribution.ContentFactory` 的返回值）；
- 面板在 `Dispose` 里解绑宿主服务事件，关闭面板时释放内容，卸载插件时关闭该插件的所有面板。

---

## 6. 宿主服务（插件能看到的数据）

`IPluginServices` 是插件访问应用状态的**唯一**通道，全部是对既有会话对象的适配，不存在第二套数据模型。

```csharp
IVolumeService Volumes   // Current(ReadonlyBig3DArray) · Version · Replaced · CurrentX/Y/Z · CrosshairChanged
                         // · Snapshot() -> VolumeSnapshot · ComputeHistogram(ct, progress) -> long[]?
IMaskService   Masks     // Layers · Add(name, Big3DBitMap, MaskColor) · Remove · NotifyContentChanged · Changed
IPreviewChannel Preview  // Publish(PreviewOverlay) · Clear(ownerId) · Overlays · Changed
IPluginLog     Log
```

要点：

- **主卷直接给核心类型，不做包装**。插件拿到 `ReadonlyBig3DArray`（非泛型基类），自己 pattern match 一次 `is ReadonlyBig3DArray<byte> / <ushort>`，然后完全用核心类型工作。
- **快照消除 TOCTOU**：`volumes.Snapshot()` 一次拿走 `Volume + Version`；后台算完对比版本后丢弃过期结果。
- **整卷统计由宿主提供**：`Volumes.ComputeHistogram(ct, progress)`，插件只负责画图。
- **掩膜是用户数据，预览是插件状态**。掩膜在插件卸载后保留；预览覆盖层随插件卸载自动清空。

### 块级读写 API（高性能遍历）

```csharp
// 卷（ReadonlyBig3DArray<T>）
int BlockCount;  int GetBlockIndex(x,y,z);  (int,int,int) GetBlockOrigin(blockIndex);
ReadOnlySpan<T> GetBlockSpan(int blockIndex);          // 零拷贝视图
bool IsBlockFullyInside(int blockIndex);
void ForEachInVolumeRange(int blockIndex, Action<int,int> range);  // 只遍历卷内体素

// 位图（Big3DBitMap）
void SetBlock(bx,by,bz, ReadOnlySpan<bool> values);    // 整块提交，自动复用空/满块
void ClearAllBlocks();
```

- **必须用 `ForEachInVolumeRange` 而不是裸 `span` 遍历**：边缘块含卷外填充（未初始化内存）。
- **不做宽化、不做整卷拷贝**。

---

## 7. 扩展点与单向依赖

`DefinePoint<T>` / `Contribute` / `GetPoint<T>` 让"插件扩展插件"成为一等公民：

```csharp
// 阈值插件发布插槽（T 是宿主标准类型 VolumeAction）
var point = context.Registry.DefinePoint<VolumeAction>("layeron.threshold.actions", "Threshold panel extensions");

// 任意其他插件贡献按钮，无需引用阈值插件程序集
context.Registry.Contribute("layeron.threshold.actions", new VolumeAction {
    Id = "myplugin.preset.lung", Header = "肺窗预设", Order = 10,
    Execute = services => services.Log.Info("…")
});
```

**类型归属规则（重要）**：扩展点的贡献类型必须是**宿主标准类型**（`Layeron.Plugin.Abstractions` 中的类型）。如果定义方用自己的类型，贡献方就得引用它在运行时的那份拷贝，而每个插件各有一份拷贝，类型不再相等 → 静默失败。

消费方用 `point.Subscribe(onAdded, onRemoved)` 并 `Track` 到作用域，卸载时自动解绑。`ExtensionPoint<T>.Add` 直调绕过作用域，仅限宿主与测试。

---

## 8. 插件作为插件加载器

```csharp
// 在插件里（返回 IDisposable，卸载时自动摘除）：
context.Registry.AddPluginSource(new MySource());
```

- 该源发现的候选一律作为**该插件的子作用域**激活 → 卸载加载器即卸载它加载的一切（任意深度），无需额外记账。
- 候选分两种：`Instance`（进程内）与 `Location`（托管程序集，宿主放进新的 collectible ALC）。

---

## 9. 参考实现：阈值分割插件

`Layeron.Plugins.Threshold` 是完整样例：

| 关注点 | 做法 |
|---|---|
| 身份 | csproj 的 `AssemblyMetadata`（§4） |
| 贡献 | `AddTool`（面板，直连）+ `AddMenu`（菜单，直连）+ `DefinePoint<VolumeAction>`（被别人扩展） |
| 对齐 | `ToolContribution.PreferredDock: DockHint` 枚举；`VolumeAction{Id, Order}` 与菜单排序对齐 |
| 泛型去重 | 每操作只 `switch` 一次 byte/ushort，其余全是单个泛型方法 |
| 性能 | 只用块 API；池化 `bool[64³]` 缓冲；`Big3DBitMap.SetBlock` 整块提交 |
| 预览 | `Preview.Publish`（XY/XZ/YZ 三个当前层同时发布） |
| 结果 | `Masks.Add` 生成普通掩膜层，之后与手动掩膜完全同权 |
| 卸载 | 面板 `Dispose` 解绑宿主事件并清预览 |
| UI | 允许并鼓励 Avalonia（XAML + ViewModel + 绑定）；手拼控件仅为当前示例写法 |

---

## 10. 脚本宿主（未完成）

`Layeron.Plugins.ScriptHost` 已实现为**普通插件 + 插件源**，当前阻塞点见Issue跟踪。`ScriptHostTests` 暂 `[Ignore]`。

---

## 11. 测试策略

无 UI 也能测：`Layeron.Plugin.Test` 用 `TestHostAdapter` 实现与 App 相同的 `IPluginHostAdapter`。

| 层次 | 覆盖 |
|---|---|
| 核心块 API | 块跨度与索引器一致性、`ForEachInVolumeRange` 覆盖集合、内部块单区间、`SetBlock` 往返 |
| 插件生命周期 | 加载/卸载/重复 id/激活抛异常回滚/管理器 Dispose |
| 作用域 | 定义者卸载 → 扩展点消失；贡献者卸载 → 贡献消失而插槽仍在；预览被清理；掩膜被保留 |
| 嵌套加载 | 子插件归属、卸载加载器级联卸载、只卸载子插件、源重载不产生重复 |
| 真实程序集 | 元数据身份、加载、卸载后 ALC 被回收、热重载得到新实例、非插件程序集静默跳过 |

---

## 12. 编写一个插件

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
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
                var snap = context.Services.Volumes.Snapshot();
                if (snap.Volume is ReadonlyBig3DArray<byte> v8)
                    context.Services.Log.Info($"voxels = {Count(v8)}");
                else if (snap.Volume is ReadonlyBig3DArray<ushort> v16)
                    context.Services.Log.Info($"voxels = {Count(v16)}");
            }
        });

        // 宿主强类型糖：过滤器与设置页走扩展方法，底层都是 Contribute
        // context.Registry.AddFilter(new MyFilter());
        // context.Registry.AddSettingsPage(new SettingsPageContribution { ... });
    }

    private static long Count<T>(ReadonlyBig3DArray<T> volume) where T : unmanaged, INumber<T>
    {
        long total = 0;
        for (var block = 0; block < volume.BlockCount; block++)
        {
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

---

## 13. 体滤波器（已实现契约，未实现驱动）

```csharp
public interface IVolumeFilter
{
    string Id { get; }
    string DisplayName { get; }
    string? Description { get; }
    void Run<T>(ReadonlyBig3DArray<T> src, Big3DArray<T> dst, FilterRegion region, FilterContext ctx)
        where T : unmanaged, INumber<T>;
}

// 注册：落在宿主拥有的 layeron.filters.volume 点上（AddFilter 扩展方法 + 全局 Id 校验）
context.Registry.AddFilter(new Mean3x3Filter());
```

规则：只写 `region` 内体素；不分配卷；不扫全卷取统计；邻域越界钳制到边缘；每块查取消、每块报进度；切片与全卷一致。

---

## 14. 已知限制

1. **脚本宿主未完成**（§10）。
2. **进程内信任模型**：插件与宿主同进程，不做沙箱；v1 依靠"安装即信任 + 加载前元数据校验"。
3. **卸载不是立即的**：`ALC.Unload()` 之后要 GC 才回收；靠 `VerifyUnloaded` 发现而不是祈祷。
4. **依赖冲突**：宿主版本优先（类型同一性 > 插件自由），插件不能替换宿主 UI 栈。
5. **热重载不迁移状态**：当前实现是卸载→重新加载。

