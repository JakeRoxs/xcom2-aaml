using global::Avalonia;
using global::Avalonia.Styling;
using AAML.Application.Settings;
using global::Avalonia.Input.Platform;

namespace AAML.Avalonia;

public interface IApplicationUiController
{
    void ApplyTheme(ThemePreference preference);
    void ApplyAccessibilitySizing(decimal textScale, decimal iconScale);
    void Shutdown(int exitCode = 0);
    Task<bool> CopyTextAsync(string text);
}

public sealed class ApplicationUiController : IApplicationUiController
{
    public void ApplyTheme(ThemePreference preference)
    {
        if (global::Avalonia.Application.Current is null) return;
        global::Avalonia.Application.Current.RequestedThemeVariant = preference switch { ThemePreference.Light => ThemeVariant.Light, ThemePreference.Dark => ThemeVariant.Dark, _ => ThemeVariant.Default };
    }

    public void ApplyAccessibilitySizing(decimal textScale, decimal iconScale)
    {
        if (global::Avalonia.Application.Current is not { } application) return;
        application.Resources["AamlBodyFontSize"] = 14d * (double)textScale;
        application.Resources["AamlSmallFontSize"] = 11d * (double)textScale;
        application.Resources["AamlBadgeFontSize"] = 12d * (double)textScale;
        application.Resources["AamlSectionTitleFontSize"] = 20d * (double)textScale;
        application.Resources["AamlPageTitleFontSize"] = 28d * (double)textScale;
        application.Resources["AamlGridRowHeight"] = Math.Max(42d, 34d * (double)textScale);
        application.Resources["AamlShellIconSize"] = 32d * (double)iconScale;
    }

    public void Shutdown(int exitCode = 0) => (global::Avalonia.Application.Current?.ApplicationLifetime as global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown(exitCode);
    public async Task<bool> CopyTextAsync(string text)
    {
        var window = (global::Avalonia.Application.Current?.ApplicationLifetime as global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (window?.Clipboard is null) return false;
        try { await window.Clipboard.SetTextAsync(text); return true; }
        catch (Exception) { return false; }
    }
}
