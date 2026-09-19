using System;
using System.Collections.Generic;
using System.Linq;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.ReactiveUI;
using Dock.Model.ReactiveUI.Controls;
using Layeron.App.ViewModels;

namespace Layeron.App;

public class DockFactory : Factory
{
    private int _windowCount = 0;
    private readonly SharedDataSourceViewModel _sharedData;
    private readonly MaskManagerViewModel _maskManager;

    public DockFactory(SharedDataSourceViewModel sharedData, MaskManagerViewModel maskManager)
    {
        _sharedData = sharedData;
        _maskManager = maskManager;
    }

    public override IRootDock CreateLayout()
    {
        var sliceAxial = new SliceViewerViewModel("Axial", "SliceAxial", SliceAxis.Axial) { SharedData = _sharedData };
        var sliceCoronal = new SliceViewerViewModel("Coronal", "SliceCoronal", SliceAxis.Coronal) { SharedData = _sharedData };
        var sliceSagittal = new SliceViewerViewModel("Sagittal", "SliceSagittal", SliceAxis.Sagittal) { SharedData = _sharedData };

        var documentDock = new DocumentDock
        {
            Id = "Documents",
            Title = "Documents",
            Proportion = 0.66,
            ActiveDockable = sliceAxial,
            VisibleDockables = CreateList<IDockable>(sliceAxial, sliceCoronal, sliceSagittal)
        };

        var leftToolDock = new ToolDock
        {
            Id = "LeftToolDock",
            Title = "LeftToolDock",
            Proportion = 0.12,
            Alignment = Alignment.Left,
            ActiveDockable = null,
            VisibleDockables = CreateList<IDockable>()
        };

        var rightToolDock = new ToolDock
        {
            Id = "RightToolDock",
            Title = "RightToolDock",
            Proportion = 0.22,
            Alignment = Alignment.Right,
            ActiveDockable = _maskManager,
            VisibleDockables = CreateList<IDockable>(_maskManager)
        };

        var mainLayout = new ProportionalDock
        {
            Id = "MainLayout",
            Title = "MainLayout",
            Orientation = Orientation.Horizontal,
            Proportion = double.NaN,
            ActiveDockable = null,
            VisibleDockables = CreateList<IDockable>(
                leftToolDock,
                new ProportionalDockSplitter(),
                documentDock,
                new ProportionalDockSplitter(),
                rightToolDock)
        };

        var rootDock = CreateRootDock();
        rootDock.Id = "Root";
        rootDock.Title = "Root";
        rootDock.ActiveDockable = mainLayout;
        rootDock.DefaultDockable = mainLayout;
        rootDock.VisibleDockables = CreateList<IDockable>(mainLayout);

        return rootDock;
    }

    public override void InitLayout(IDockable layout)
    {
        ContextLocator = new Dictionary<string, Func<object>>
        {
            ["Root"] = () => new DocumentWindowViewModel(),
            ["MainLayout"] = () => new DocumentWindowViewModel(),
            ["Documents"] = () => new DocumentWindowViewModel(),
            ["SliceAxial"] = () => new SliceViewerViewModel("Axial", "SliceAxial", SliceAxis.Axial) { SharedData = _sharedData },
            ["SliceCoronal"] = () => new SliceViewerViewModel("Coronal", "SliceCoronal", SliceAxis.Coronal) { SharedData = _sharedData },
            ["SliceSagittal"] = () => new SliceViewerViewModel("Sagittal", "SliceSagittal", SliceAxis.Sagittal) { SharedData = _sharedData },
            ["LeftToolDock"] = () => new ToolWindowViewModel(),
            ["RightToolDock"] = () => new ToolWindowViewModel(),
            ["MaskManager"] = () => _maskManager
        }!;

        DockableLocator = new Dictionary<string, Func<IDockable>>
        {
            ["Root"] = () => layout,
            ["Documents"] = () => layout,
            ["LeftToolDock"] = () => layout,
            ["RightToolDock"] = () => layout
        }!;

        HostWindowLocator = new Dictionary<string, Func<IHostWindow>>
        {
            [nameof(IDockWindow)] = () => new HostWindow()
        }!;

        base.InitLayout(layout);
    }

    public IHostWindow CreateWindow()
    {
        _windowCount++;
        var window = new HostWindow();
        window.Title = $"Window {_windowCount}";
        return window;
    }

    public void AddDocument(IRootDock root, IDockable newDockable)
    {
        // Dock into the currently active document dock when one exists. This
        // works even when the layout has several document groups, because we
        // resolve the dock dynamically instead of tracking a fixed instance.
        if (FindActiveDock<IDocumentDock>(root) is { } documentDock)
        {
            AddDockable(documentDock, newDockable);
            SetActiveDockable(newDockable);
            SetFocusedDockable(documentDock, newDockable);
            return;
        }

        // There is no document dock left in the layout (e.g. the last document
        // was closed), so open the new document as a floating window.
        OpenAsFloatingWindow(root, newDockable);
    }

    public void AddTool(IRootDock root, IDockable newTool)
    {
        // Dock into the currently active tool dock when one exists.
        if (FindActiveDock<IToolDock>(root) is { } toolDock)
        {
            AddDockable(toolDock, newTool);
            SetActiveDockable(newTool);
            SetFocusedDockable(toolDock, newTool);
            return;
        }

        // There is no tool dock left in the layout, so open the tool as a
        // floating window.
        OpenAsFloatingWindow(root, newTool);
    }

    /// <summary>
    /// Finds the dock of the given kind that currently has focus, falling back
    /// to the first one present in the layout. Returns null when no such dock
    /// exists (e.g. it collapsed after its last item was closed).
    /// </summary>
    private TDock? FindActiveDock<TDock>(IRootDock root)
        where TDock : class, IDock
    {
        // Walk up the ownership chain from the currently focused dockable so we
        // resolve the active dock when several groups are open.
        for (var current = CurrentDockable; current is not null; current = current.Owner as IDockable)
        {
            if (current is TDock dock)
            {
                return dock;
            }
        }

        return FindDockable(root, static d => d is TDock) as TDock;
    }

    private void OpenAsFloatingWindow(IRootDock root, IDockable dockable)
    {
        var window = CreateWindowFrom(dockable);
        if (window is null) return;

        window.Title = dockable.Title;
        AddWindow(root, window);
        window.Present(window.IsModal);
    }
}
