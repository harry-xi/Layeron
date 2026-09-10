using System;
using System.Collections.Generic;
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
        var sliceXY = new SliceViewerViewModel("XY Slice", "SliceXY", SliceAxis.XY) { SharedData = _sharedData };
        var sliceXZ = new SliceViewerViewModel("XZ Slice", "SliceXZ", SliceAxis.XZ) { SharedData = _sharedData };
        var sliceYZ = new SliceViewerViewModel("YZ Slice", "SliceYZ", SliceAxis.YZ) { SharedData = _sharedData };

        var documentDock = new DocumentDock
        {
            Id = "Documents",
            Title = "Documents",
            Proportion = 0.66,
            ActiveDockable = sliceXY,
            VisibleDockables = CreateList<IDockable>(sliceXY, sliceXZ, sliceYZ)
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
            ["SliceXY"] = () => new SliceViewerViewModel("XY Slice", "SliceXY", SliceAxis.XY) { SharedData = _sharedData },
            ["SliceXZ"] = () => new SliceViewerViewModel("XZ Slice", "SliceXZ", SliceAxis.XZ) { SharedData = _sharedData },
            ["SliceYZ"] = () => new SliceViewerViewModel("YZ Slice", "SliceYZ", SliceAxis.YZ) { SharedData = _sharedData },
            ["LeftToolDock"] = () => new ToolWindowViewModel(),
            ["RightToolDock"] = () => new ToolWindowViewModel(),
            ["MaskManager"] = () => _maskManager
        };

        DockableLocator = new Dictionary<string, Func<IDockable>>
        {
            ["Root"] = () => layout,
            ["Documents"] = () => layout,
            ["LeftToolDock"] = () => layout,
            ["RightToolDock"] = () => layout
        };

        HostWindowLocator = new Dictionary<string, Func<IHostWindow>>
        {
            [nameof(IDockWindow)] = () => new HostWindow()
        };

        base.InitLayout(layout);
    }

    public IHostWindow CreateWindow()
    {
        _windowCount++;
        var window = new HostWindow();
        window.Title = $"Window {_windowCount}";
        return window;
    }
}
