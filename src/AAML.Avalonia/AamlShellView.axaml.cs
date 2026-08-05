using global::Avalonia;
using global::Avalonia.Controls;

namespace AAML.Avalonia;

public sealed partial class AamlShellView : UserControl
{
    public static readonly StyledProperty<bool> IsRailOpenProperty =
        AvaloniaProperty.Register<AamlShellView, bool>(nameof(IsRailOpen), true);

    public AamlShellView() => InitializeComponent();

    public bool IsRailOpen
    {
        get => GetValue(IsRailOpenProperty);
        set => SetValue(IsRailOpenProperty, value);
    }
}
