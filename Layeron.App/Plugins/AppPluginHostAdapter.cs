using System;
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
    public ObservableCollection<MenuDescription> Menus { get; } = [];

    /// <summary>Type ids of tools contributed by plugins, for the window's plugin tool list.</summary>
    public ObservableCollection<ContributedTool> Tools { get; } = [];

    /// <summary>Openable documents contributed by plugins, for the window's plugin document list.</summary>
    public ObservableCollection<ContributedDocument> Documents { get; } = [];

    public IDockableContribution AttachTool(string pluginId, ToolContribution tool)
    {
        var typeId = DockableIds.ForPluginTool(pluginId, tool.Id);
        var factory = tool.ContentFactory;

        _dock.Factory.Register(typeId, descriptor =>
            new PluginToolViewModel(
                descriptor.Title ?? tool.Title,
                typeId,
                () => BuildContent(typeId, factory),
                tool.CanClose));

        var entry = new ContributedTool(pluginId, tool.Title, typeId, tool.PreferredDock.ToString().ToLowerInvariant());
        Tools.Add(entry);

        return new DockableHandle(
            undo: () =>
            {
                _dock.Factory.Unregister(typeId);
                Tools.Remove(entry);

                // Close any open panel of this type: it belongs to a plugin that is going away, and
                // leaving it docked would keep plugin content alive after unload.
                if (_dock.Layout is { } root)
                    _dock.Factory.CloseDockables(root, d => d.Id == typeId);
            },
            open: () => _dock.OpenToolById(typeId, tool.Title));
    }

    public IDockableContribution AttachDocument(string pluginId, DocumentContribution document)
    {
        var typeId = DockableIds.ForPluginDocument(pluginId, document.Id);
        var factory = document.ContentFactory;

        _dock.Factory.Register(typeId, descriptor =>
            new PluginDocumentViewModel(
                descriptor.Title ?? document.Title,
                typeId,
                () => BuildContent(typeId, factory),
                document.CanClose));

        var entry = new ContributedDocument(pluginId, document.Title, typeId);
        Documents.Add(entry);

        return new DockableHandle(
            undo: () =>
            {
                _dock.Factory.Unregister(typeId);
                Documents.Remove(entry);

                // Close the open tab: it belongs to a plugin that is going away, and leaving it docked
                // would keep plugin content (and its GPU resources) alive after unload.
                if (_dock.Layout is { } root)
                    _dock.Factory.CloseDockables(root, d => d.Id == typeId);
            },
            open: () => _dock.OpenDocumentById(typeId, document.Title));
    }

    public IDisposable AttachMenu(string pluginId, MenuContribution menu)
    {
        var description = new MenuDescription(menu.Path, menu.Header, menu.Execute, menu.CanExecute, menu.Order);
        Menus.Add(description);
        return new Handle(() => Menus.Remove(description));
    }

    /// <summary>
    /// Builds a plugin control the first time its panel or tab is shown. The constructor may do real
    /// work (start an interpreter, open a model), so it is deliberately kept out of layout restore.
    /// </summary>
    private static object BuildContent(string typeId, Func<object?>? factory)
    {
        if (!StartupTrace.Enabled)
            return factory?.Invoke() ?? "(plugin content has no factory)";

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            return factory?.Invoke() ?? "(plugin content has no factory)";
        }
        finally
        {
            StartupTrace.Mark(
                $"plugin-content-built:{typeId}:{System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}ms");
        }
    }

    private sealed class DockableHandle(Action undo, Action open) : IDockableContribution
    {
        private bool _disposed;

        // Opening focuses an already-open panel of this type or creates it on demand; the dock decides,
        // so the plugin never has to know the host's dockable type ids.
        public void Open()
        {
            if (!_disposed)
                open();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            undo();
        }
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

/// <summary>A document a plugin contributed, as listed in the window's plugin menu.</summary>
public sealed record ContributedDocument(string PluginId, string Title, string TypeId);
