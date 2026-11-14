using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using CtrlZ.App.ViewModels;

namespace CtrlZ.App.Views;

public partial class InstallView : UserControl
{
    private InstallViewModel? _viewModel;

    public InstallView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.Logs.CollectionChanged -= Logs_CollectionChanged;
                _viewModel.Items.CollectionChanged -= Items_CollectionChanged;
            }

            _viewModel = DataContext as InstallViewModel;
            if (_viewModel is not null)
            {
                _viewModel.Logs.CollectionChanged += Logs_CollectionChanged;
                _viewModel.Items.CollectionChanged += Items_CollectionChanged;
            }
        };
    }

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (QueueList.Items.Count > 0)
                {
                    QueueList.ScrollIntoView(QueueList.Items[0]);
                }
            });
        }
    }

    private void Logs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_viewModel?.AutoScroll != true)
        {
            return;
        }

        // Scrolling synchronously inside CollectionChanged can re-enter the item container
        // generator while WPF is still applying the change. Queue it after layout instead.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            if (LogList.Items.Count > 0)
            {
                LogList.ScrollIntoView(LogList.Items[^1]);
            }
        });
    }

    private void OpenScan_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _ = _viewModel.NavigateToAsync("scan");
        }
    }

    private void OpenCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _ = _viewModel.NavigateToAsync("catalog");
        }
    }

    private void OpenMyApps_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _ = _viewModel.NavigateToAsync("apps");
        }
    }
}
