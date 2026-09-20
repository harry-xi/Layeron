using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Layeron.App.Layout;
using Layeron.App.ViewModels;
using Layeron.Plugin.Abstractions;
using Layeron.Plugin.Host;

namespace Layeron.App.Plugins;

/// <summary>
/// Bridges plugin contributions into the application: tool contributions become dockable factories
/// registered with <see cref="DockFactory"/>, menu contributions become entries in the window's
/// plugin menu.
/// <para>
/// Every attach call returns a removal handle, and the plugin manager disposes those handles when the
/// plugin unloads. That is the whole unload design: nothing here needs to know which plugin it is
/// tearing down, only how to undo one registration.
/// </para>
/// </summary>
public sealed class AppPluginHostAdapter : IPluginHostAdapter
{
    private readonly DockViewModel _dock;

    public AppPluginHostAdapter(DockViewModel dock, AppPluginServices services, IPluginLog log)
    {
        _dock = dock;
        Services = services;
        Log = log;
    }

    public IPluginServices Services { get; }

    public IPluginLog Log { get; }

    /// <summary>Contributed menu entries, in registration order; the window renders them as a tree.</summary>
    public ObservableCollection<MenuDescription> Menus { get; } = new();

    /// <summary>Type ids of tools contributed by plugins, for the window's plugin tool list.</summary>
    public ObservableCollection<ContributedTool> Tools { get; } = new();

    public IDisposable AttachTool(string pluginId, ToolContribution tool)
    {
        var typeId = DockableIds.ForPluginTool(pluginId, tool.Id);
        var factory = tool.ContentFactory;

        _dock.Factory.Register(typeId, descriptor =>
        {
            var content = factory?.Invoke() ?? "(plugin tool has no content)";
            return new PluginToolViewModel(descriptor.Title ?? tool.Title, typeId, content, tool.CanClose);
        });

        var entry = new ContributedTool(pluginId, tool.Title, typeId, tool.PreferredDock);
        Tools.Add(entry);

        return new Handle(() =>
        {
            _dock.Factory.Unregister(typeId);
            Tools.Remove(entry);

            // Close any open panel of this type: it belongs to a plugin that is going away, and
            // leaving it docked would keep plugin content alive after unload.
            if (_dock.Layout is { } root)
                _dock.Factory.CloseDockables(root, d => d.Id == typeId);
        });
    }

    public IDisposable AttachMenu(string pluginId, MenuContribution menu)
    {
        var description = new MenuDescription(menu.Path, menu.Header, menu.Execute, menu.CanExecute, menu.Order);
        Menus.Add(description);
        return new Handle(() => Menus.Remove(description));
    }

    private sealed class Handle(Action undo) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            undo();
        }
    }
}

/// <summary>A tool a plugin contributed, as listed in the window's plugin menu.</summary>
public sealed record ContributedTool(string PluginId, string Title, string TypeId, string? PreferredDock);
