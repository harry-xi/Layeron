# 插件加载器

**插件本身也可以是插件加载器。** 通过注册一个 `IPluginSource`，插件获得"发现并激活其他插件"的能力——它发现的一切都成为该插件的**子作用域**，所以卸载它就会连带卸载它加载的一切。

这就是脚本宿主的实现方式：它是一个普通插件，把每个 `.csx` 文件包成一个 `IPlugin` 实例。宿主没有任何改动。

## 为什么需要它

扩展"插件的格式"不需要改宿主：

| 格式 | 加载器 |
|---|---|
| `.dll` | 宿主的程序集扫描器 |
| `.csx`（C# 脚本） | `layeron.scripthost` 插件 |
| Python / F# / 远程源 | 任何第三方插件，用同样的方式 |

## IPluginSource

```csharp
public interface IPluginSource
{
    string Id { get; }                                          // 稳定 id，例如 layeron.scripthost.csharp
    string? Description { get; }                                // 诊断信息
    IEnumerable<PluginDescriptor> Discover(IPluginSourceContext context);
}
```

`Discover` 枚举**当前能提供**的候选。它返回 `PluginDescriptor`，其中 `Instance` 由加载器自己创建（进程内），或者 `Location` 指向一个由宿主在 collectible 上下文里加载的程序集。

## IPluginSourceContext

传给 `Discover`，让加载器不必猜测宿主的目录布局：

```csharp
public interface IPluginSourceContext
{
    string PluginDirectory { get; }                 // 宿主扫描的根插件目录（可能不存在）
    string DevelopmentPluginDirectory { get; }      // 开发构建目录（优先于已发布副本）
    IReadOnlyCollection<string> LoadedPluginIds { get; }   // 已加载的 id，用于跳过已知会被拒绝的工作
    IPluginLog Log { get; }                          // 作用域到该源的日志
}
```

## 注册

```csharp
context.Registry.AddPluginSource(new MySource());
```

返回一个 `IDisposable`：释放它，这个源提供的一切都会卸载。宿主在插件卸载时会自动这样做。

注册之后，宿主会在**合适的时机**调用 `Discover` 并激活结果。它不会在 `Activate` 返回前同步做完——而是排入待处理队列，由 `RunPendingSources()` 统一处理，这样加载顺序不会因为"谁先注册"而变得难以推理。

## 参考实现：脚本源

```csharp
internal sealed class ScriptPluginSource : IPluginSource
{
    public string Id => "layeron.scripthost.csharp";
    public string? Description => "C# script plugins (*.csx)";

    // 开发目录在前：本地副本遮蔽已发布的同名脚本，与宿主对程序集的做法一致。
    public IReadOnlyList<string> ResolveScriptDirectories(IPluginSourceContext context) =>
    [
        ScriptPaths.PluginScriptsDirectory(context.DevelopmentPluginDirectory),
        ScriptPaths.PluginScriptsDirectory(context.PluginDirectory)
    ];

    public IEnumerable<PluginDescriptor> Discover(IPluginSourceContext context)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in ResolveScriptDirectories(context))
        {
            if (!Directory.Exists(directory))
                continue;

            foreach (var file in Directory
                         .EnumerateFiles(directory, "*" + ScriptPaths.Extension, SearchOption.TopDirectoryOnly)
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileNameWithoutExtension(file);

                // 开发副本优先
                if (!seen.Add(name))
                    continue;

                // 候选在进程内创建：脚本宿主自己拥有这份代码，
                // 没有必要为一个它自己编译的脚本再建一个加载上下文。
                yield return new PluginDescriptor
                {
                    Id = $"script:{name}",
                    Name = $"Script: {name}",
                    Version = File.GetLastWriteTimeUtc(file).ToString("u"),
                    Description = file,
                    Origin = file,
                    Instance = new ScriptPlugin(file, context.Log)
                };
            }
        }
    }
}
```

## 写自己的加载器

### 1. 决定候选从哪来

`Discover` 可以做任何事：扫描文件夹、读清单、查询服务、解释脚本。约束只有两个：

- 它应该**快**：每次重载都会调用，不要在里面做重活（真正的初始化放在 `IPlugin.Activate` 里）；
- 它应该**尊重 `LoadedPluginIds`**，跳过已知会被去重掉的候选。

### 2. 决定由谁创建实例

| 你的情况 | 选哪个 | 说明 |
|---|---|---|
| 候选是托管程序集，希望隔离加载 | `Location` | 宿主在自己的 collectible 上下文里加载它 |
| 代码由加载器自己解释 / 编译 | `Instance` | 加载器拥有代码，不需要额外上下文 |

脚本宿主用 `Instance`，因为 Roslyn 编译出的程序集由**默认上下文**加载，脚本宿主再为它建一个上下文没有意义。

### 3. 让 Discover 幂等

`Discover` 可能被多次调用（重载、重新扫描）。用 `context.LoadedPluginIds` 与自己的记忆避免重复产出。

### 4. 子作用域是自动的

被发现的插件**自动**成为加载器的子作用域：

```
layeron.scripthost（插件）
  ├─ script:hello（子作用域）
  ├─ script:stats（子作用域）
  └─ script:batch（子作用域）

卸载 layeron.scripthost → 三个脚本一起卸载
```

## 宿主视角：PluginManager

如果你在写自己的宿主（测试、批处理工具），入口是 `PluginManager`：

```csharp
public sealed class PluginManagerOptions
{
    public string PluginDirectory { get; set; }            // 默认 %LOCALAPPDATA%\Layeron\plugins
    public string DevelopmentPluginDirectory { get; set; } // 默认 <应用目录>\plugins.dev
    public bool EnforceApiVersion { get; set; } = true;
    public bool VerifyUnload { get; set; } = true;
}

var manager = new PluginManager(adapter, new PluginManagerOptions());
```

| 方法 | 作用 |
|---|---|
| `LoadAll()` | 扫描插件目录 + 运行所有插件源，返回新加载的 id |
| `LoadDirectory(path)` | 扫描指定目录 |
| `LoadAssembly(path)` | 加载单个程序集 |
| `LoadInProcess(plugin, id, ...)` | 激活一个进程内实例 |
| `RunPendingSources()` | 处理待发现的插件源 |
| `Reload(pluginId)` | 卸载后重新加载 |
| `Unload(pluginId)` | 卸载 |
| `UnloadAll()` | 全部卸载 |
| `ReloadSource(sourceId)` | 让某个源重新枚举 |
| `ReloadAllSources()` | 让所有源重新枚举 |
| `VerifyUnloaded()` | 弱引用自检，返回仍存活的插件 id |
| `Plugins` / `LoadedPluginIds` / `Changed` / `PointRegistry` | 状态与事件 |

### 宿主适配器

`PluginManager` 不引用任何 UI 框架；"贡献显示在哪里"由宿主通过 `IPluginHostAdapter` 提供：

```csharp
public interface IPluginHostAdapter
{
    IPluginServices Services { get; }
    IPluginLog Log { get; }

    IDockableContribution AttachTool(string pluginId, ToolContribution tool);
    IDockableContribution AttachDocument(string pluginId, DocumentContribution document);
    IDisposable AttachMenu(string pluginId, MenuContribution menu);
}
```

`Attach*` 必须返回一个能撤销贡献的句柄；插件卸载时，管理器会**逆序**释放这些句柄。

实现一个适配器（甚至一个不做任何 UI 的适配器）就能让插件系统在无界面环境里完整运行——`Layeron.Plugin.Test` 的 `TestHostAdapter` 就是这样做测试的。
