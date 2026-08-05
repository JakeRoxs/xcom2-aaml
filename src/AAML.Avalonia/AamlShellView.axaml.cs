using global::Avalonia;
using global::Avalonia.Controls;
using AAML.Application.Settings;

namespace AAML.Avalonia;

public sealed partial class AamlShellView : UserControl
{
    public static readonly StyledProperty<bool> IsRailOpenProperty =
        AvaloniaProperty.Register<AamlShellView, bool>(nameof(IsRailOpen), true);
    private ApplicationSession? session;
    private bool synchronizing;
    private bool railModeChanged;

    public AamlShellView() : this(NavigationRailMode.Expanded) { }

    internal AamlShellView(NavigationRailMode initialMode)
    {
        InitializeComponent();
        synchronizing = true;
        IsRailOpen = initialMode != NavigationRailMode.Compact;
        synchronizing = false;
    }

    public bool IsRailOpen
    {
        get => GetValue(IsRailOpenProperty);
        set => SetValue(IsRailOpenProperty, value);
    }

    public void Configure(ApplicationSession applicationSession)
    {
        if (ReferenceEquals(session, applicationSession)) return;
        session = applicationSession;
        if (railModeChanged)
        {
            _ = PersistRailModeAsync(IsRailOpen ? NavigationRailMode.Expanded : NavigationRailMode.Compact);
            return;
        }
        synchronizing = true;
        IsRailOpen = applicationSession.Settings?.NavigationRailMode != NavigationRailMode.Compact;
        synchronizing = false;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsRailOpenProperty || synchronizing) return;
        railModeChanged = true;
        if (session is not null) _ = PersistRailModeAsync(IsRailOpen ? NavigationRailMode.Expanded : NavigationRailMode.Compact);
    }

    private async Task PersistRailModeAsync(NavigationRailMode mode)
    {
        try { await session!.SetNavigationRailModeAsync(mode, CancellationToken.None); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
