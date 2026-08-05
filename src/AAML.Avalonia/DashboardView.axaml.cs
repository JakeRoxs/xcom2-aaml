using global::Avalonia.Controls;

namespace AAML.Avalonia;

public sealed partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => (DataContext as DashboardViewModel)?.Activate();
    }

    private void OnModRootsChanged(object? sender, TextChangedEventArgs args)
    {
        if (sender is TextBox textBox && DataContext is DashboardViewModel viewModel) viewModel.ModRoots = textBox.Text ?? string.Empty;
    }

    private void OnLaunchArgumentsChanged(object? sender, TextChangedEventArgs args)
    {
        if (sender is TextBox textBox && DataContext is DashboardViewModel viewModel) viewModel.LaunchArguments = textBox.Text ?? string.Empty;
    }
}
