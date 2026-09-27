using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Layeron.App.ViewModels;

namespace Layeron.App.Views;

public partial class LogViewerView : UserControl
{
    private INotifyCollectionChanged? _events;
    private LogViewerViewModel? _viewModel;

    public LogViewerView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as LogViewerViewModel);
        AttachedToVisualTree += (_, _) => ScrollToEnd();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
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
