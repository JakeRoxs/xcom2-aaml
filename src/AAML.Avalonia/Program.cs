using global::Avalonia;
using ReactiveUI.Avalonia;
using Avalonia.Threading;

namespace AAML.Avalonia;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) => FatalErrorCoordinator.Handle(eventArgs.ExceptionObject as Exception ?? new InvalidOperationException("A non-exception object reached the app-domain boundary."), "app-domain", false);
        TaskScheduler.UnobservedTaskException += (_, eventArgs) => { FatalErrorCoordinator.RecordNonFatal(eventArgs.Exception, "task-unobserved"); eventArgs.SetObserved(); };
        try
        {
            Dispatcher.UIThread.UnhandledException += (_, eventArgs) => { eventArgs.Handled = true; FatalErrorCoordinator.Handle(eventArgs.Exception, "avalonia-dispatcher", true); };
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception) { FatalErrorCoordinator.Handle(exception, "entry-point", false); return 1; }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .UseReactiveUI(_ => { })
        .LogToTrace();
}
