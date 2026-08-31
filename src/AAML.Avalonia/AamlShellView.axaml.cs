using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
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
    public static readonly DirectProperty<AamlShellView, string> ActiveGameDisplayNameProperty =
        AvaloniaProperty.RegisterDirect<AamlShellView, string>(nameof(ActiveGameDisplayName), o => o.ActiveGameDisplayName, unsetValue: string.Empty);
    public static readonly DirectProperty<AamlShellView, bool> IsChallengeModeProperty =
        AvaloniaProperty.RegisterDirect<AamlShellView, bool>(nameof(IsChallengeMode), o => o.IsChallengeMode);
    private ApplicationSession? session;
    private bool synchronizing;
    private bool railModeChanged;
    private GameVariant? activeGame;
    private readonly Dictionary<GameVariant, IImage?> gameIconCache = new();

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
            var previousDisplayName = ActiveGameDisplayName;
            var previousChallengeMode = IsChallengeMode;
            activeGame = value;
            RaisePropertyChanged(ActiveGameProperty, previousGame, value);
            RaisePropertyChanged(ActiveGameIconProperty, previousIcon, ActiveGameIcon);
            RaisePropertyChanged(ActiveGameIconTooltipProperty, previousTooltip, ActiveGameIconTooltip);
            RaisePropertyChanged(ActiveGameDisplayNameProperty, previousDisplayName, ActiveGameDisplayName);
            RaisePropertyChanged(IsChallengeModeProperty, previousChallengeMode, IsChallengeMode);
        }
    }

    public string ActiveGameIconTooltip
    {
        get => ActiveGameDisplayName;
    }

    public string ActiveGameDisplayName
    {
        get => activeGame is null ? "AAML" : GameVariantDisplay.GetDisplayName(activeGame.Value);
    }

    public bool IsChallengeMode
    {
        get => GameVariantDisplay.IsChallengeMode(activeGame);
    }

    public IReadOnlyList<GameOption> GameOptions
    {
        get => Enum.GetValues<GameVariant>()
            .Select(variant => new GameOption(
                variant,
                GameVariantDisplay.GetSelectorDisplayName(variant),
                GetGameIcon(variant),
                variant == activeGame))
            .ToArray();
    }

    private IImage? GetGameIcon(GameVariant game)
    {
        if (!gameIconCache.TryGetValue(game, out var icon))
        {
            icon = LoadGameIcon(game);
            gameIconCache[game] = icon;
        }

        return icon;
    }

    private static IImage? LoadGameIcon(GameVariant? game)
    {
        var iconPath = game switch
        {
            GameVariant.XCom2 => "Assets/games/game-xcom2.png",
            GameVariant.XCom2WarOfTheChosen => "Assets/games/game-xcom2-wotc.png",
            GameVariant.XCom2WarOfTheChosenChallengeMode => "Assets/games/game-xcom2-wotc-challenge.png",
            GameVariant.ChimeraSquad => "Assets/games/game-chimera.png",
            _ => "Assets/aaml-icon.png"
        };

        var assemblyName = typeof(App).Assembly.GetName().Name ?? nameof(AAML.Avalonia);
        var iconUri = new UriBuilder("avares", assemblyName)
        {
            Path = iconPath
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

    public IImage? ActiveGameIcon
    {
        get => activeGame is null ? LoadGameIcon(null) : GetGameIcon(activeGame.Value);
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

    private void OnGameIconButtonClicked(object? sender, RoutedEventArgs e)
    {
        if (GameSelectorPopup is not { } popup) return;
        popup.IsOpen = !popup.IsOpen;
    }

    private async void OnGameOptionClicked(object? sender, RoutedEventArgs e)
    {
        CloseGameSelector();

        if (sender is not Button { Tag: GameVariant variant }) return;
        var activeSession = session;
        if (activeSession is null) return;

        await activeSession.SelectGameAsync(variant, CancellationToken.None);
    }

    private void OnGameSelectorPopupOpened(object? sender, EventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            topLevel.PointerPressed += OnGameSelectorOutsidePointerPressed;
        }
    }

    private void OnGameSelectorPopupClosed(object? sender, EventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            topLevel.PointerPressed -= OnGameSelectorOutsidePointerPressed;
        }
    }

    private void OnGameSelectorOutsidePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (GameSelectorPopup is not { IsOpen: true, Child: { } child } popup) return;
        if (e.Source is not Visual source) return;

        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, child) || ReferenceEquals(current, popup)) return;
            current = current.Parent as Visual;
        }

        CloseGameSelector();
    }

    private void CloseGameSelector()
    {
        if (GameSelectorPopup is { IsOpen: true } popup)
        {
            popup.IsOpen = false;
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
