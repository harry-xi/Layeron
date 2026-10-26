# 生命周期与作用域

Layeron 的插件生命周期只有一个方向：**注册，然后被撤销**。没有 `Shutdown`，没有 `Deactivate`，没有"请记得清理"的清单。

## 一次完整的生命周期

```
                   ┌─ 发现（MetadataReader 读程序集元数据，不执行插件代码）
                   │
   加载 ────────────┼─ 校验身份与 API 版本
                   │
                   └─ 创建 collectible AssemblyLoadContext
                          │
   激活 ───────────────────┴─ IPlugin.Activate(context)
                                 │
                                 ├─ context.Registry.AddTool(...)
                                 ├─ context.Registry.AddMenu(...)
                                 ├─ context.Registry.DefinePoint<T>(...)
                                 ├─ context.Registry.Track(...)
                                 └─ context.Services.Log.Info(...)
                                        │
   运行 ────────────────────────────────┘   （贡献生效，走宿主 UI）
                                        │
   卸载 ─────────────────────────────────┴─ context.Dispose()
                                              ├─ 先释放子作用域（递归）
                                              ├─ 再逆序释放本作用域已登记的注册
                                              └─ 卸载 ALC → 弱引用自检
```

## IPluginContext：作用域

`IPluginContext` 是插件拿到的唯一入口，也是它的生命周期对象：

| 成员 | 说明 |
|---|---|
| `PluginId` | 本插件的稳定 id |
| `PluginName` | 人类可读名称（已知时） |
| `Services` | 宿主服务：体数据、掩膜、预览、日志 |
| `Registry` | 贡献面：`AddTool` / `AddDocument` / `AddMenu` / `DefinePoint` / `Contribute` / `AddPluginSource` / `Track` |
| `CreateChild(name)` | 创建嵌套作用域 |
| `IsDisposed` | 是否已释放；释放后不得再注册 |

`IPluginContext : IDisposable`——释放它是**框架**做的事（卸载插件时），插件自己不调用它。

### 逆序回滚

注册的副作用按**注册的逆序**释放。这与依赖关系一致：后注册的东西可能依赖先注册的东西。

### Track：把任意 IDisposable 挂到插件的生命上

```csharp
// 一个定时器、一个 CancellationTokenSource、一个事件订阅包装……
context.Registry.Track(new Timer(...));   // 或你自己的 IDisposable
```

`Track` 是让"非注册类"资源也享受作用域管理的出口。事件订阅也可以用扩展方法更自然地处理：

```csharp
var point = context.Registry.GetPoint<VolumeAction>("some.point");
if (point != null)
    context.Registry.Track(point.Subscribe(
        onAdded: action => UpdateUi(action),
        onRemoved: action => UpdateUi(null)));
```

`Subscribe` 返回一个取消订阅的句柄；`Track` 让它随插件一起结束。**忘记 `Track` 是插件卸载后仍被引用（从而无法回收）的最常见原因。**

## 子作用域

`CreateChild` 创建嵌套作用域：子作用域的贡献可以**单独提前释放**，但父作用域释放时也一定会释放它们。

```csharp
// 一组必须一起拆掉的注册，生命周期短于插件本身
using var scope = context.CreateChild("one-import");
scope.Registry.AddTool(new ToolContribution { Id = "preview", Title = "导入预览", /* ... */ });
// 离开 using，这个面板就消失了；插件的其他贡献不受影响
```

插件加载器用的正是这个机制：被发现的插件成为加载它的插件的**子作用域**，因此卸载加载器会连带卸载它加载的一切。

## 卸载验证

`PluginManagerOptions.VerifyUnload`（默认 `true`）会在卸载后尝试回收插件的加载上下文，并报告仍然存活的插件：

```csharp
var leaked = manager.VerifyUnloaded();
foreach (var id in leaked)
    log.Warn("Plugin {Id} is still referenced after unload", id);
```

"卸载"因此是一个**可验证的事实**。典型泄漏原因：

| 原因 | 修法 |
|---|---|
| 订阅了宿主事件但没有解绑 | 在视图的 `Dispose` 里解绑，或 `Registry.Track(subscription)` |
| 后台线程 / 定时器仍在运行 | `Registry.Track(theTimer)` |
| 视图持有宿主服务引用且未释放 | 让 `ContentFactory` 返回的对象实现 `IDisposable` |
| 静态字段缓存了插件对象 | 不要用静态状态保存插件实例 |
| 把宿主标准类型之外的插件类型传给了扩展点 | 贡献类型的程序集必须由默认上下文加载 |

::: tip 为什么视图的 Dispose 至关重要
`ContentFactory` 返回的对象如果实现 `IDisposable`，宿主会在**关闭面板或标签页**以及**卸载插件**时释放它。这是视图解绑宿主事件、停掉线程、释放 GPU 资源的唯一时机。视图不释放，插件的加载上下文就回收不掉。
:::

## 热加载

重载是"卸载 + 加载"的组合，没有额外的机制：

```csharp
manager.Reload("acme.hello");     // 卸载旧的，重新发现并激活
manager.ReloadAllSources();        // 重新枚举所有插件源（含脚本）
```

`plugins.dev` 里的开发构建优先于已安装的同名插件，所以"改代码 → 构建 → 重载"是一个完整的迭代循环。

## 无 UI 宿主

生命周期与 UI 无关。测试与批处理宿主实现一个最小的 `IPluginHostAdapter` 即可完整地加载、卸载、验证插件：

| 关注点 | 无 UI 宿主 |
|---|---|
| `Activate` | 正常调用（`ContentFactory` 不一定会被调用） |
| 工具 / 文档贡献 | 记录在适配器里，不创建控件 |
| UI 插件 | 入口类型发现容忍无法物化的视图类型，所以仍可加载 |
| 脚本 | 没有 `IScriptHostService` 时仍能发现并激活插件目录里的脚本，只是没有编辑器 |

这也是为什么插件文档反复强调"`Activate` 里不要碰 UI 类型"：把控件创建放进 `ContentFactory`，插件就天然可以在无 UI 宿主里跑。
