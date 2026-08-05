using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AAML.Avalonia;

public partial class ConfigurationsView : UserControl
{
    private ConfigurationsViewModel? viewModel;
    private SynchronizedTextViewScroll? synchronizedScroll;
    private IniColorizingTransformer? editorTransformer;
    private IniColorizingTransformer? leftTransformer;
    private IniColorizingTransformer? rightTransformer;
    private bool synchronizing;

    public ConfigurationsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        Attach(DataContext as ConfigurationsViewModel);
        ConfigurationEditor.TextChanged += OnEditorTextChanged;
        ConfigurationEditor.TextArea.SelectionChanged += OnSelectionChanged;
        var normal = ConfigurationEditor.Foreground ?? Brushes.Gray;
        var comment = ResourceBrush("SystemControlForegroundBaseMediumBrush", normal);
        var accent = ResourceBrush("SystemControlHighlightAccentBrush", normal);
        editorTransformer = new IniColorizingTransformer(comment, accent, accent);
        leftTransformer = new IniColorizingTransformer(comment, accent, accent);
        rightTransformer = new IniColorizingTransformer(comment, accent, accent);
        ConfigurationEditor.TextArea.TextView.LineTransformers.Add(editorTransformer);
        LeftDiffEditor.TextArea.TextView.LineTransformers.Add(leftTransformer);
        RightDiffEditor.TextArea.TextView.LineTransformers.Add(rightTransformer);
        synchronizedScroll = new SynchronizedTextViewScroll(LeftDiffEditor.TextArea.TextView, RightDiffEditor.TextArea.TextView);
    }

    private void OnUnloaded(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        ConfigurationEditor.TextChanged -= OnEditorTextChanged;
        ConfigurationEditor.TextArea.SelectionChanged -= OnSelectionChanged;
        synchronizedScroll?.Dispose();
        synchronizedScroll = null;
        if (editorTransformer is not null) ConfigurationEditor.TextArea.TextView.LineTransformers.Remove(editorTransformer);
        if (leftTransformer is not null) LeftDiffEditor.TextArea.TextView.LineTransformers.Remove(leftTransformer);
        if (rightTransformer is not null) RightDiffEditor.TextArea.TextView.LineTransformers.Remove(rightTransformer);
        editorTransformer = leftTransformer = rightTransformer = null;
        Attach(null);
    }

    private void Attach(ConfigurationsViewModel? next)
    {
        if (viewModel is not null) viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        viewModel = next;
        if (viewModel is not null)
        {
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            SynchronizeEditors();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConfigurationsViewModel.EditorText) or nameof(ConfigurationsViewModel.LeftDiffText) or nameof(ConfigurationsViewModel.RightDiffText)) SynchronizeEditors();
    }

    private void SynchronizeEditors()
    {
        if (viewModel is null) return;
        synchronizing = true;
        try
        {
            if (!string.Equals(ConfigurationEditor.Text, viewModel.EditorText, StringComparison.Ordinal)) ConfigurationEditor.Text = viewModel.EditorText;
            if (!string.Equals(LeftDiffEditor.Text, viewModel.LeftDiffText, StringComparison.Ordinal)) LeftDiffEditor.Text = viewModel.LeftDiffText;
            if (!string.Equals(RightDiffEditor.Text, viewModel.RightDiffText, StringComparison.Ordinal)) RightDiffEditor.Text = viewModel.RightDiffText;
        }
        finally { synchronizing = false; }
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (!synchronizing) viewModel?.UpdateEditorText(ConfigurationEditor.Text);
    }

    private void OnSelectionChanged(object? sender, EventArgs e) => viewModel?.UpdateSelection(ConfigurationEditor.SelectionStart, ConfigurationEditor.SelectionLength);

    private IBrush ResourceBrush(string key, IBrush fallback) => global::Avalonia.Application.Current?.TryGetResource(key, ActualThemeVariant, out var value) == true && value is IBrush brush ? brush : fallback;
}
