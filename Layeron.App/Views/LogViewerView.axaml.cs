using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Layeron.App.ViewModels;
using Serilog.Events;

namespace Layeron.App.Views;

public partial class LogViewerView : UserControl
{
    private INotifyCollectionChanged? _events;
    private LogViewerViewModel? _viewModel;

    public LogViewerView()
    {
        StartupTrace.Mark("view:LogViewerView:begin");
        InitializeComponent();
        StartupTrace.Mark("view:LogViewerView:end");
        DataContextChanged += (_, _) => Attach(DataContext as LogViewerViewModel);
        AttachedToVisualTree += (_, _) => ScrollToEnd();

        // Right-click to copy one record. It is wired here rather than declared on the row: a context
        // menu is its own visual tree, so a binding to the window's view model from inside it cannot
        // resolve - the same reason the main window wires its menus in code.
        this.FindControl<ListBox>("EventList")?.AddHandler(
            PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Offers to copy the record under the pointer. The row is selected first, so the pane shows the same
    /// record the menu was opened on; the copy itself is the only action the menu has.
    /// </summary>
    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel is null || !e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            return;

        if (RowOf(e.Source as Visual) is not { } row || row.DataContext is not LogEvent logEvent)
            return;

        if (this.FindControl<ListBox>("EventList") is { } list)
            list.SelectedItem = logEvent;

        var copy = new MenuItem { Header = "复制这条记录" };
        copy.Click += (_, _) => _ = _viewModel.CopyRecordAsync(logEvent);

        new ContextMenu { ItemsSource = new object[] { copy } }.Open(row);
    }

    /// <summary>The row container the pointer is inside, or null when it is not over a record.</summary>
    private static ListBoxItem? RowOf(Visual? source)
    {
        for (var current = source; current is not null; current = current.GetVisualParent())
        {
            if (current is ListBoxItem item)
                return item;

            // Stop at the list itself: a right-click on empty space below the rows is not a record.
            if (current is ListBox)
                return null;
        }

        return null;
    }

    /// <summary>
    /// Follows the newest event while <see cref="LogViewerViewModel.Follow"/> is on. Scrolling is the
    /// view's job, so the view model stays free of control references; the work is posted rather than
    /// done inline because the new row has not been laid out yet when the collection changes.
    /// </summary>
    private void Attach(LogViewerViewModel? viewModel)
    {
        if (_events != null)
            _events.CollectionChanged -= OnEventsChanged;

        if (_viewModel != null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = viewModel;
        _events = viewModel?.Events;

        if (_events != null)
            _events.CollectionChanged += OnEventsChanged;

        if (_viewModel != null)
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogViewerViewModel.Follow) && _viewModel?.Follow == true)
            ScrollToEnd();
    }

    private void OnEventsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && _viewModel?.Follow == true)
            ScrollToEnd();
    }

    private void ScrollToEnd()
        => Dispatcher.UIThread.Post(() =>
        {
            if (_viewModel?.Events.Count > 0)
                this.FindControl<ListBox>("EventList")?.ScrollIntoView(_viewModel.Events[^1]);
        }, DispatcherPriority.Background);
}
