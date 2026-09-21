using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Layeron.App.ViewModels;

namespace Layeron.App.Views;

public partial class LogViewerView : UserControl
{
    private INotifyCollectionChanged? _records;
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
    /// Follows the newest record while <see cref="LogViewerViewModel.Follow"/> is on. Scrolling is the
    /// view's job, so the view model stays free of control references; the work is posted rather than
    /// done inline because the new row has not been laid out yet when the collection changes.
    /// </summary>
    private void Attach(LogViewerViewModel? viewModel)
    {
        if (_records != null)
            _records.CollectionChanged -= OnRecordsChanged;

        if (_viewModel != null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = viewModel;
        _records = viewModel?.Records;

        if (_records != null)
            _records.CollectionChanged += OnRecordsChanged;

        if (_viewModel != null)
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogViewerViewModel.Follow) && _viewModel?.Follow == true)
            ScrollToEnd();
    }

    private void OnRecordsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && _viewModel?.Follow == true)
            ScrollToEnd();
    }

    private void ScrollToEnd()
        => Dispatcher.UIThread.Post(() =>
        {
            if (_viewModel?.Records.Count > 0)
                this.FindControl<ListBox>("RecordList")?.ScrollIntoView(_viewModel.Records[^1]);
        }, DispatcherPriority.Background);
}
