using AAML.Application.Ports;

namespace AAML.Infrastructure.Linux.Paths;

public sealed record LinuxApplicationPathOptions(
    string HomeDirectory,
    string? ConfigurationHome,
    string? DataHome,
    string? StateHome,
    string? CacheHome,
    string? RuntimeHome,
    string RuntimeFallbackDirectory);

/// <summary>Explicit XDG paths selected without current-directory fallbacks.</summary>
public sealed class LinuxApplicationPaths : IApplicationPaths
{
    private readonly LinuxPathSemantics semantics = new();

    public LinuxApplicationPaths(LinuxApplicationPathOptions options, string applicationDirectory = "aaml")
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
        if (applicationDirectory.Contains('/') || applicationDirectory is "." or "..") throw new ArgumentException("Application directory must be one safe path component.", nameof(applicationDirectory));
        var home = RequiredAbsolute(options.HomeDirectory, nameof(options.HomeDirectory));
        ConfigurationDirectory = Join(Select(options.ConfigurationHome, home + "/.config"), applicationDirectory);
        DataDirectory = Join(Select(options.DataHome, home + "/.local/share"), applicationDirectory);
        StateDirectory = Join(Select(options.StateHome, home + "/.local/state"), applicationDirectory);
        CacheDirectory = Join(Select(options.CacheHome, home + "/.cache"), applicationDirectory);
        UsesRuntimeFallback = !IsAbsolute(options.RuntimeHome);
        RuntimeDirectory = Join(UsesRuntimeFallback ? RequiredAbsolute(options.RuntimeFallbackDirectory, nameof(options.RuntimeFallbackDirectory)) : options.RuntimeHome!, applicationDirectory);
    }

    public string ConfigurationDirectory { get; }
    public string DataDirectory { get; }
    public string StateDirectory { get; }
    public string CacheDirectory { get; }
    public string RuntimeDirectory { get; }
    public bool UsesRuntimeFallback { get; }

    private string Select(string? candidate, string fallback) => IsAbsolute(candidate) ? semantics.NormalizeIdentity(candidate!).Value! : fallback;
    private bool IsAbsolute(string? value) => !string.IsNullOrWhiteSpace(value) && semantics.NormalizeIdentity(value).IsSuccess;
    private string RequiredAbsolute(string value, string parameter) => semantics.NormalizeIdentity(value).IsSuccess ? semantics.NormalizeIdentity(value).Value! : throw new ArgumentException("The path must be an absolute POSIX path.", parameter);
    private string Join(string root, string component) => root == "/" ? "/" + component : root.TrimEnd('/') + "/" + component;
}
