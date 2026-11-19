using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Layeron.App.ViewModels;
using Layeron.Plugin.Abstractions;
using Layeron.Plugin.Host;

namespace Layeron.App.Plugins;

/// <summary>
/// The application's plugin surface: owns the <see cref="PluginManager"/>, exposes what is loaded to
/// the UI, and provides load / unload / hot-reload operations.
/// <para>
/// Loading happens on the UI thread before the window is shown, so contributed dockable factories are
/// registered before a saved layout is restored - a plugin panel saved in a layout resolves normally
/// instead of being reported as unresolved.
/// </para>
/// </summary>
public sealed class PluginController : IDisposable
{
    private readonly AppPluginServices _services;
    private readonly AppPluginHostAdapter _adapter;
    private readonly PluginManager _manager;

    /// <summary>
    /// Creates the plugin surface.
    /// </summary>
    /// <param name="options">
    /// Where plugins are found. Tests pass temporary directories so a run never loads the user's installed
    /// plugins - loading them would execute their code inside the test process.
    /// </param>
    public PluginController(AppPluginServices services, DockViewModel dock, PluginManagerOptions? options = null)
    {
        _services = services;
        _adapter = new AppPluginHostAdapter(dock, _services, _services.Log);
        _manager = new PluginManager(_adapter, options ?? new PluginManagerOptions());

        // 设置窗口的插件扩展点由宿主拥有：先定义再加载，点随宿主存活,
        // 只有各插件的贡献随其卸载摘除。
        _manager.PointRegistry.DefinePoint<SettingsPageContribution>(SettingsPoints.Pages, "设置页面");

        _adapter.Menus.CollectionChanged += (_, _) => RebuildMenuTree();

        _manager.Changed += () =>
        {
            RefreshLoadedList();
            RebuildMenuTree();
            RefreshToolList();
            RefreshDocumentList();
        };
    }

    /// <summary>Plugins currently loaded, as shown in the plugin manager menu.</summary>
    public ObservableCollection<LoadedPlugin> Loaded { get; } = [];

    /// <summary>Tools contributed by loaded plugins.</summary>
    public ObservableCollection<ContributedTool> Tools { get; } = [];

    /// <summary>Documents contributed by loaded plugins.</summary>
    public ObservableCollection<ContributedDocument> Documents { get; } = [];

    /// <summary>Nested menu tree built from contributed paths.</summary>
    public IReadOnlyList<PluginMenuNode> MenuNodes { get; private set; } = [];

    /// <summary>Raised when <see cref="MenuNodes"/> changed, so the window can rebuild its menu.</summary>
    public event Action? MenuChanged;

    /// <summary>Raised when <see cref="Tools"/> changed.</summary>
    public event Action? ToolsChanged;

    /// <summary>Raised when <see cref="Documents"/> changed.</summary>
    public event Action? DocumentsChanged;

    /// <summary>Raised when <see cref="Loaded"/> changed.</summary>
    public event Action? PluginsChanged;

    /// <summary>设置页扩展点（宿主拥有），设置窗口订阅其增删以追加插件页。</summary>
    public ExtensionPoint<SettingsPageContribution>? SettingsPoint =>
        _manager.PointRegistry.GetPoint<SettingsPageContribution>(SettingsPoints.Pages);

    /// <summary>
    /// The host-owned volume-filter point. The filter runner window binds to it through
    /// <see cref="Layeron.App.Filters.FilterCatalog"/> so contributions follow plugin load, unload and hot reload.
    /// </summary>
    public ExtensionPoint<IVolumeFilter>? FilterPoint =>
        _manager.PointRegistry.GetPoint<IVolumeFilter>(FilterPoints.Volume);

    /// <summary>
    /// The volume loaders the import menu offers: the host's built-in readers plus every one a plugin
    /// contributed. Read through the manager's point registry, so it always reflects what is loaded.
    /// </summary>
    public IReadOnlyList<VolumeLoader> Loaders => VolumeLoaderRunner.All(_manager.PointRegistry);    /// <summary>Root plugin directory, shown in diagnostics.</summary>
    public string PluginDirectory => _manager.Options.PluginDirectory;

    /// <summary>Development plugin directory (scanned first, so local builds shadow installed ones).</summary>
    public string DevelopmentPluginDirectory => _manager.Options.DevelopmentPluginDirectory;

    /// <summary>App-owned shared-contract directory next to the executable.</summary>
    public string SharedPluginDirectory => _manager.Options.SharedPluginDirectory;

    /// <summary>User-writable shared-contract directory, additive to the app-owned one.</summary>
    public string UserSharedPluginDirectory => _manager.Options.UserSharedPluginDirectory;

    /// <summary>Loads every plugin from the configured directories.</summary>
    public IReadOnlyList<string> LoadAll()
    {
        var loaded = _manager.LoadAll();
        RefreshLoadedList();
        return loaded;
    }

    /// <summary>Initializes the plugin system, loading all plugins from the configured directories.</summary>
    public IReadOnlyList<string> InitLoad()
    {
        var loaded = _manager.InitLoad();
        RefreshLoadedList();
        return loaded;
    }

    /// <summary>Enables all plugins.</summary>
    internal void EnableAll()
    {
        throw new NotImplementedException();
    }

    /// <summary>Loads a single plugin assembly (used by tests and by "load from file" tooling).</summary>
    public string? LoadAssembly(string path) => _manager.LoadAssembly(path);

    /// <summary>Runs plugin sources again, e.g. after a script was installed into a plugin directory.</summary>
    public IReadOnlyList<string> RefreshSources() => _manager.ReloadAllSources();

    /// <summary>Unloads a plugin, including everything it loaded as a plugin loader.</summary>
    public bool Unload(string pluginId) => _manager.Unload(pluginId);

    /// <summary>Hot reloads a plugin: unload, then load again from the same location.</summary>
    public bool Reload(string pluginId) => _manager.Reload(pluginId);

    /// <summary>
    /// Unloads everything and verifies each load context was actually collected. Returns the ids that
    /// leaked, so a regression in teardown shows up as a number rather than as mysterious memory growth.
    /// </summary>
    public IReadOnlyList<string> VerifyUnload()
    {
        var leaked = _manager.VerifyUnloaded();
        if (leaked.Count > 0)
            _services.Log.Warn($"Plugin load contexts still alive after unload: {string.Join(", ", leaked)}");
        return leaked;
    }

    /// <summary>Opens a contributed tool panel in the dock, bringing it to the front if already open.</summary>
    public void OpenTool(ContributedTool tool, DockViewModel dock)
    {
        if (!dock.OpenToolById(tool.TypeId, tool.Title))
            _services.Log.Warn($"Could not open tool '{tool.Title}' ({tool.TypeId}).");
    }

    /// <summary>Opens a contributed document tab in the centre dock, focusing it if already open.</summary>
    public void OpenDocument(ContributedDocument document, DockViewModel dock)
    {
        if (!dock.OpenDocumentById(document.TypeId, document.Title))
            _services.Log.Warn($"Could not open document '{document.Title}' ({document.TypeId}).");
    }

    private void RefreshLoadedList()
    {
        Loaded.Clear();
        foreach (var plugin in _manager.Plugins)
            Loaded.Add(plugin);
        PluginsChanged?.Invoke();
    }

    private void RefreshToolList()
    {
        Tools.Clear();
        foreach (var tool in _adapter.Tools)
            Tools.Add(tool);
        ToolsChanged?.Invoke();
    }

    private void RefreshDocumentList()
    {
        Documents.Clear();
        foreach (var document in _adapter.Documents)
            Documents.Add(document);
        DocumentsChanged?.Invoke();
    }

    private void RebuildMenuTree()
    {
        MenuNodes = PluginMenuNode.Build(_adapter.Menus);
        MenuChanged?.Invoke();
    }

    /// <summary>Creates the plugin directories so a first run has somewhere to drop plugins.</summary>
    public void EnsureDirectories()
    {
        foreach (var directory in new[] { PluginDirectory, DevelopmentPluginDirectory, UserSharedPluginDirectory })
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                _services.Log.Warn($"Could not create plugin directory '{directory}': {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        // UnloadAll first (which tears down every plugin), then verify collection, then release the
        // services the plugins were using.
        _manager.Dispose();
        _services.Dispose();
    }
}

