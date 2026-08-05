using Avalonia;
using Avalonia.Headless;
using ReactiveUI.Avalonia;

namespace AAML.Avalonia.Tests;

internal static class AvaloniaTestHost
{
    private static readonly Lazy<HeadlessUnitTestSession> LazySession = new(() => HeadlessUnitTestSession.StartNew(typeof(AvaloniaTestApp)));

    public static HeadlessUnitTestSession Session => LazySession.Value;

    private static class AvaloniaTestApp
    {
        public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .UseReactiveUI(_ => { });
    }
}
