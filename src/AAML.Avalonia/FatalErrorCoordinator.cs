using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using AAML.Application.Diagnostics;
using AAML.Application.Logging;
using AAML.Application.Ports;
using AAML.Application;

namespace AAML.Avalonia;

internal static class FatalErrorCoordinator
{
    private static int handling;
    private static IApplicationDiagnostics? diagnostics;
    private static IExternalLauncher? launcher;
    private static IApplicationUiController? ui;
    public static void Configure(IApplicationDiagnostics applicationDiagnostics, IExternalLauncher externalLauncher, IApplicationUiController controller) { diagnostics = applicationDiagnostics; launcher = externalLauncher; ui = controller; }
    public static void RecordNonFatal(Exception exception, string boundary) { try { diagnostics?.Write(LocalLogLevel.Error, "exception.unobserved_task", exception.Message, new Dictionary<string, string> { ["boundary"] = boundary, ["type"] = exception.GetType().FullName ?? exception.GetType().Name }); } catch { Console.Error.WriteLine(exception); } }
    public static void Handle(Exception exception, string boundary, bool showDialog)
    {
        if (Interlocked.Exchange(ref handling, 1) != 0) return;
        var report = diagnostics?.BuildReport(exception, boundary) ?? $"AAML fatal error\nBoundary: {boundary}\n{exception}";
        try { diagnostics?.Write(LocalLogLevel.Error, "exception.fatal", exception.Message, new Dictionary<string, string> { ["boundary"] = boundary, ["type"] = exception.GetType().FullName ?? exception.GetType().Name }); } catch { Console.Error.WriteLine(report); }
        if (!showDialog || ui is null) { Console.Error.WriteLine(report); return; }
        Dispatcher.UIThread.Post(() => Show(report));
    }
    private static void Show(string report)
    {
        try
        {
            var status = new TextBlock { Text = "A local diagnostic entry was written. No information was uploaded. Review the report before sharing.", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap };
            var copy = new Button { Content = "Copy report" }; copy.Click += async (_, _) => status.Text = await ui!.CopyTextAsync(report) ? "Report copied. Review it before sharing." : "Clipboard is unavailable.";
            var logs = new Button { Content = "Open logs folder" }; logs.Click += async (_, _) => { Directory.CreateDirectory(diagnostics!.LogDirectory); await launcher!.OpenDirectoryAsync(diagnostics.LogDirectory, CancellationToken.None); };
            var issue = new Button { Content = "Report issue" }; issue.Click += async (_, _) => await launcher!.OpenUriAsync(ProjectIdentity.IssuesUri, CancellationToken.None);
            var close = new Button { Content = "Close AAML" }; close.Click += (_, _) => ui!.Shutdown(1);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { copy, logs, issue, close } };
            var window = new Window { Title = "AAML stopped unexpectedly", Icon = App.CreateWindowIcon(), Width = 760, Height = 560, Content = new StackPanel { Margin = new global::Avalonia.Thickness(20), Spacing = 12, Children = { new TextBlock { Text = "An unexpected error prevented AAML from continuing.", FontSize = 20, FontWeight = global::Avalonia.Media.FontWeight.SemiBold }, status, new TextBox { Text = report, IsReadOnly = true, AcceptsReturn = true, Height = 390 }, buttons } } };
            window.Closed += (_, _) => ui!.Shutdown(1); window.Show();
        }
        catch { Console.Error.WriteLine(report); ui?.Shutdown(1); }
    }
}
