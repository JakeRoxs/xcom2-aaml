using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using AAML.Application.Common;
using AAML.Application.Logging;
using AAML.Application.Ports;

namespace AAML.Application.Diagnostics;

public interface IApplicationDiagnostics
{
    string LogDirectory { get; }
    string ActiveLogPath { get; }
    void Write(LocalLogLevel level, string eventName, string message, IReadOnlyDictionary<string, string>? properties = null);
    string BuildReport(Exception? exception = null, string boundary = "manual");
    Task<Result> FlushAsync(CancellationToken cancellationToken);
}

public interface IGameLogLocator
{
    string? GetCurrentLogPath(AAML.Domain.Games.GameVariant variant);
}

public sealed class UnavailableGameLogLocator : IGameLogLocator
{
    public string? GetCurrentLogPath(AAML.Domain.Games.GameVariant variant) => null;
}

public sealed class ApplicationDiagnostics(ILocalLog log, IApplicationPaths paths, IApplicationVersionProvider versionProvider) : IApplicationDiagnostics
{
    public string LogDirectory { get; } = Path.Combine(paths.StateDirectory, "Logs");
    public string ActiveLogPath => Path.Combine(LogDirectory, "aaml.log");
    public void Write(LocalLogLevel level, string eventName, string message, IReadOnlyDictionary<string, string>? properties = null) => log.Write(level, eventName, Redact(message), properties?.ToDictionary(item => item.Key, item => Redact(item.Value)));
    public Task<Result> FlushAsync(CancellationToken cancellationToken) => log.FlushAsync(cancellationToken);

    public string BuildReport(Exception? exception = null, string boundary = "manual")
    {
        var version = versionProvider.GetCurrentVersion();
        var text = $"AAML diagnostic report\nIncident: {Guid.NewGuid():N}\nTimestamp UTC: {DateTimeOffset.UtcNow:O}\nVersion: {(version.IsSuccess ? version.Value : "unknown")}\nRuntime: {RuntimeInformation.FrameworkDescription}\nOS: {RuntimeInformation.OSDescription}\nArchitecture: {RuntimeInformation.ProcessArchitecture}\nBoundary: {boundary}\nLocal diagnostics only: no information was uploaded.\n";
        if (exception is not null) text += $"Exception:\n{exception}\n";
        return Redact(text);
    }

    private string Redact(string value)
    {
        var replacements = new[] { (paths.RuntimeDirectory, "<RUNTIME>"), (paths.ConfigurationDirectory, "<CONFIG>"), (paths.DataDirectory, "<DATA>"), (paths.StateDirectory, "<STATE>"), (paths.CacheDirectory, "<CACHE>"), (AppContext.BaseDirectory, "<APP>"), (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<HOME>"), (Environment.UserName, "<USER>"), (Environment.MachineName, "<MACHINE>") };
        foreach (var (source, marker) in replacements.Where(item => !string.IsNullOrWhiteSpace(item.Item1)).OrderByDescending(item => item.Item1.Length)) value = value.Replace(source, marker, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        value = Regex.Replace(value, @"(?i)(password|passwd|secret|token|api[-_]?key|authorization|bearer|cookie|session|dsn)\s*[:=]\s*[^\s,;]+", "$1=<REDACTED>");
        value = Regex.Replace(value, @"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}", "<EMAIL>", RegexOptions.IgnoreCase);
        return value;
    }
}
