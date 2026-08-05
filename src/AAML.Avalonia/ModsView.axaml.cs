using global::Avalonia.Controls;
using global::Avalonia.Threading;
using System.Collections.Specialized;

namespace AAML.Avalonia;

public sealed partial class ModsView : UserControl
{
    private readonly HashSet<AAML.Domain.Mods.ModKey> selectedKeys = [];
    private ModsViewModel? subscribedViewModel;
    private bool restorePending;
    private bool restoringSelection;

    public ModsView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => (DataContext as ModsViewModel)?.Activate();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (subscribedViewModel is not null) subscribedViewModel.Rows.CollectionChanged -= OnRowsChanged;
        subscribedViewModel = DataContext as ModsViewModel;
        if (subscribedViewModel is not null) subscribedViewModel.Rows.CollectionChanged += OnRowsChanged;
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (restorePending || selectedKeys.Count == 0) return;
        restorePending = true;
        Dispatcher.UIThread.Post(() => { restorePending = false; RestoreSelection(); }, DispatcherPriority.Background);
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (restoringSelection) return;
        var rows = ModsGrid.SelectedItems.Cast<SessionModRow>().ToArray();
        if (rows.Length == 0 && DataContext is ModsViewModel { Rows.Count: 0 }) return;
        selectedKeys.Clear();
        foreach (var key in rows.Select(row => row.Key).OfType<AAML.Domain.Mods.ModKey>()) selectedKeys.Add(key);
        if (DataContext is ModsViewModel viewModel) viewModel.SetSelection(rows);
    }

    private void RestoreSelection()
    {
        if (selectedKeys.Count == 0 || ModsGrid.ItemsSource is null) return;
        restoringSelection = true;
        try
        {
            foreach (var row in ModsGrid.ItemsSource.Cast<SessionModRow>().Where(row => row.Key is { } key && selectedKeys.Contains(key)))
                if (!ModsGrid.SelectedItems.Contains(row)) ModsGrid.SelectedItems.Add(row);
        }
        finally { restoringSelection = false; }
    }
}
