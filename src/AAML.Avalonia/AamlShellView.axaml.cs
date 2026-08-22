using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Platform;
using global::Avalonia.Media;
using global::Avalonia.Media.Imaging;
using AAML.Application.Settings;
using AAML.Domain.Games;

namespace AAML.Avalonia;

public sealed partial class AamlShellView : UserControl
{
    public static readonly StyledProperty<bool> IsRailOpenProperty =
        AvaloniaProperty.Register<AamlShellView, bool>(nameof(IsRailOpen), true);
    public static readonly DirectProperty<AamlShellView, GameVariant?> ActiveGameProperty =
        AvaloniaProperty.RegisterDirect<AamlShellView, GameVariant?>(nameof(ActiveGame), o => o.ActiveGame, (o, v) => o.ActiveGame = v);
    public static readonly DirectProperty<AamlShellView, IImage?> ActiveGameIconProperty =
        AvaloniaProperty.RegisterDirect<AamlShellView, IImage?>(nameof(ActiveGameIcon), o => o.ActiveGameIcon, unsetValue: null);
    public static readonly DirectProperty<AamlShellView, string> ActiveGameIconTooltipProperty =
        AvaloniaProperty.RegisterDirect<AamlShellView, string>(nameof(ActiveGameIconTooltip), o => o.ActiveGameIconTooltip, unsetValue: string.Empty);
    private ApplicationSession? session;
    private bool synchronizing;
    private bool railModeChanged;
    private GameVariant? activeGame;

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

    public GameVariant? ActiveGame
    {
        get => activeGame;
        private set
        {
            if (activeGame == value) return;
            var previousGame = activeGame;
            var previousIcon = ActiveGameIcon;
            var previousTooltip = ActiveGameIconTooltip;
            activeGame = value;
            RaisePropertyChanged(ActiveGameProperty, previousGame, value);
            RaisePropertyChanged(ActiveGameIconProperty, previousIcon, ActiveGameIcon);
            RaisePropertyChanged(ActiveGameIconTooltipProperty, previousTooltip, ActiveGameIconTooltip);
        }
    }

    public string ActiveGameIconTooltip
    {
        get => activeGame switch
        {
            GameVariant.XCom2 => "XCOM 2",
            GameVariant.XCom2WarOfTheChosen => "XCOM 2: War of the Chosen",
            GameVariant.XCom2WarOfTheChosenChallengeMode => "XCOM 2: WOTC Challenge Mode",
            GameVariant.ChimeraSquad => "Chimera Squad",
            _ => "AAML"
        };
    }

    public IImage? ActiveGameIcon
    {
        get
        {
            var fileName = activeGame switch
            {
                GameVariant.XCom2 => "game-xcom2.png",
                GameVariant.XCom2WarOfTheChosen => "game-xcom2-wotc.png",
                GameVariant.XCom2WarOfTheChosenChallengeMode => "game-xcom2-wotc-challenge.png",
                GameVariant.ChimeraSquad => "game-chimera.png",
                _ => "aaml-icon.png"
            };

            var assemblyName = typeof(App).Assembly.GetName().Name ?? nameof(AAML.Avalonia);
            var iconUri = new UriBuilder("avares", assemblyName)
            {
                Path = $"Assets/games/{fileName}"
            }.Uri;

            try
            {
                return new Bitmap(AssetLoader.Open(iconUri));
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public void Configure(ApplicationSession applicationSession)
    {
        if (ReferenceEquals(session, applicationSession)) return;
        session = applicationSession;
        session.PropertyChanged += OnSessionPropertyChanged;
        if (railModeChanged)
        {
            _ = PersistRailModeAsync(IsRailOpen ? NavigationRailMode.Expanded : NavigationRailMode.Compact);
            return;
        }
        synchronizing = true;
        IsRailOpen = applicationSession.Settings?.NavigationRailMode != NavigationRailMode.Compact;
        synchronizing = false;
        if (session.Settings is { } settings)
        {
            ActiveGame = settings.SelectedGame;
        }
    }

    private void OnSessionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ApplicationSession.Settings) && session is { Settings: { } settings })
        {
            ActiveGame = settings.SelectedGame;
        }
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
        var activeSession = session;
        if (activeSession is null) return;

        try
        {
            await activeSession.SetNavigationRailModeAsync(mode, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The rail mode is a non-critical preference and should not fail the shell.
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        ClearSession();
    }

    public void ClearSession()
    {
        if (session is null) return;

        session.PropertyChanged -= OnSessionPropertyChanged;
        session.Dispose();
        session = null;
    }
}
