using AAML.Application.Ports;

namespace AAML.Infrastructure.Windows.Paths;

/// <summary>Per-user Windows storage paths that do not depend on current directory.</summary>
public sealed class WindowsApplicationPaths : IApplicationPaths
{
    public WindowsApplicationPaths(string localApplicationDataDirectory, string applicationDirectory = "AAML")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
        if (applicationDirectory.IndexOfAny(['\\', '/', ':']) >= 0 || applicationDirectory is "." or "..") throw new ArgumentException("Application directory must be one safe path component.", nameof(applicationDirectory));
        var root = localApplicationDataDirectory.Replace('/', '\\').TrimEnd('\\') + "\\" + applicationDirectory;
        ConfigurationDirectory = root + "\\Config";
        DataDirectory = root + "\\Data";
        StateDirectory = root + "\\State";
        CacheDirectory = root + "\\Cache";
        RuntimeDirectory = root + "\\Runtime";
    }

    public string ConfigurationDirectory { get; }
    public string DataDirectory { get; }
    public string StateDirectory { get; }
    public string CacheDirectory { get; }
    public string RuntimeDirectory { get; }
}
