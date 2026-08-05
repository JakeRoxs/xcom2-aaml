using System.Globalization;
using System.Runtime.Versioning;
using AAML.Application.Common;
using AAML.Application.Steam;
using AAML.Infrastructure.Common.Steam;
using Microsoft.Win32;

namespace AAML.Infrastructure.Windows.Steam;

internal interface IWindowsSteamRootLocator
{
    IReadOnlyList<string> GetRoots();
}

internal sealed class WindowsSteamRootLocator : IWindowsSteamRootLocator
{
    [SupportedOSPlatform("windows")]
    public IReadOnlyList<string> GetRoots()
    {
        var roots = new List<string>();
        Add(Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"), "SteamPath", roots);
        Add(Registry.LocalMachine.OpenSubKey(@"Software\WOW6432Node\Valve\Steam"), "InstallPath", roots);
        Add(Registry.LocalMachine.OpenSubKey(@"Software\Valve\Steam"), "InstallPath", roots);
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    [SupportedOSPlatform("windows")]
    private static void Add(RegistryKey? key, string valueName, ICollection<string> roots)
    {
        using (key)
        {
            if (key?.GetValue(valueName) is string value && !string.IsNullOrWhiteSpace(value)) roots.Add(value);
        }
    }
}

/// <summary>Discovers Windows Steam libraries and installed application manifests without initializing Steamworks.</summary>
public sealed class WindowsSteamFilesystemDiscovery : ISteamFilesystemDiscovery
{
    private readonly IWindowsSteamRootLocator rootLocator;

    public WindowsSteamFilesystemDiscovery() : this(new WindowsSteamRootLocator()) { }
    internal WindowsSteamFilesystemDiscovery(IWindowsSteamRootLocator rootLocator) => this.rootLocator = rootLocator;

    public Task<Result<SteamGameDiscovery>> DiscoverAsync(SteamDiscoveryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.AppIds is null || request.AppIds.Count != 1 || request.AppIds.Any(id => id.Value == 0))
            return Task.FromResult(Failure("steam.app_id_invalid", "Exactly one nonzero Steam app ID is required.", ErrorKind.Validation));
        if ((request.CandidateSteamRoots is null || request.CandidateSteamRoots.Count == 0) && !OperatingSystem.IsWindows())
            return Task.FromResult(Failure("steam.discovery_platform_unsupported", "Registry-based Steam discovery requires Windows.", ErrorKind.Unavailable));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = request.CandidateSteamRoots is { Count: > 0 } ? request.CandidateSteamRoots : rootLocator.GetRoots();
            var roots = candidates.Where(root => !string.IsNullOrWhiteSpace(root)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToArray();
            if (roots.Length == 0) return Task.FromResult(Failure("steam.no_installation_found", "No Windows Steam installation was found.", ErrorKind.NotFound));
            var installations = roots.Select(root => new SteamInstallation(root, root, SteamInstallationKind.Native, Path.Combine(root, "steamapps"), Path.Combine(root, "userdata"))).ToArray();
            var libraries = new List<SteamLibrary>();
            foreach (var installation in installations)
            {
                libraries.Add(CreateLibrary(installation.RootPath, null, SteamLibrarySource.Root));
                var libraryFile = Path.Combine(installation.SteamAppsPath, "libraryfolders.vdf");
                if (!File.Exists(libraryFile)) continue;
                foreach (var (index, path) in ReadLibraries(libraryFile)) libraries.Add(CreateLibrary(path, index, SteamLibrarySource.LibraryFoldersVdf));
            }
            var uniqueLibraries = libraries.GroupBy(library => library.RootPath, StringComparer.OrdinalIgnoreCase).Select(group => group.OrderBy(library => library.Source).First()).ToArray();
            return Task.FromResult(Result<SteamGameDiscovery>.Success(DiscoverApplication(request.AppIds[0], installations, uniqueLibraries, cancellationToken)));
        }
        catch (OperationCanceledException) { return Task.FromResult(Failure("steam.discovery_cancelled", "Steam discovery was cancelled.", ErrorKind.Cancelled)); }
        catch (FormatException exception) { return Task.FromResult(Failure("steam.library_file_invalid", exception.Message, ErrorKind.InvalidData)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return Task.FromResult(Failure("steam.discovery_io", exception.Message, ErrorKind.Io));
        }
    }

    private static SteamGameDiscovery DiscoverApplication(SteamAppId appId, IReadOnlyList<SteamInstallation> installations, IReadOnlyList<SteamLibrary> libraries, CancellationToken cancellationToken)
    {
        var applications = new List<SteamInstalledApplication>();
        var workshops = new List<SteamWorkshopLocation>();
        var diagnostics = new List<DiscoveryDiagnostic>();
        foreach (var library in libraries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = Path.Combine(library.SteamAppsPath, $"appmanifest_{appId.Value}.acf");
            if (!File.Exists(manifest)) continue;
            var fields = ReadFields(manifest);
            if (fields.TryGetValue("appid", out var declared) && declared != appId.Value.ToString(CultureInfo.InvariantCulture))
            {
                diagnostics.Add(Diagnostic("steam.manifest_app_id_mismatch", ErrorKind.InvalidData, "Manifest app ID does not match its filename.", appId, manifest));
                continue;
            }
            if (!fields.TryGetValue("installdir", out var installDirectory) || !IsSafeDirectoryName(installDirectory))
            {
                diagnostics.Add(Diagnostic("steam.install_dir_invalid", ErrorKind.InvalidData, "Manifest install directory is missing or unsafe.", appId, manifest));
                continue;
            }
            var gamePath = Path.Combine(library.CommonPath, installDirectory);
            applications.Add(new SteamInstalledApplication(appId, library.RootPath, manifest, installDirectory, gamePath, fields.GetValueOrDefault("name"), fields.GetValueOrDefault("StateFlags"), true, Directory.Exists(gamePath)));
            var contentRoot = Path.Combine(library.WorkshopPath, "content", appId.Value.ToString(CultureInfo.InvariantCulture));
            workshops.Add(new SteamWorkshopLocation(appId, library.RootPath, contentRoot, Directory.Exists(contentRoot)
                ? Directory.EnumerateDirectories(contentRoot).Where(path => ulong.TryParse(Path.GetFileName(path), out _)).Order(StringComparer.OrdinalIgnoreCase).ToArray()
                : []));
        }
        var installed = applications.Where(application => application.InstallDirectoryExists).ToArray();
        if (installed.Length == 0) diagnostics.Add(new DiscoveryDiagnostic("steam.game_install_missing", ErrorKind.NotFound, "Steam manifests did not identify an installed copy of the game.", new Dictionary<string, string> { ["appId"] = appId.Value.ToString(CultureInfo.InvariantCulture) }));
        if (installed.Length > 1) diagnostics.Add(new DiscoveryDiagnostic("steam.game_install_ambiguous", ErrorKind.Conflict, "More than one library contains an installed copy of the game.", new Dictionary<string, string> { ["appId"] = appId.Value.ToString(CultureInfo.InvariantCulture) }));
        return new SteamGameDiscovery(appId, installations, libraries, applications, workshops, [], [], diagnostics);
    }

    private static IEnumerable<(int Index, string Path)> ReadLibraries(string path)
    {
        foreach (var entry in ValveKeyValueParser.Parse(File.ReadAllText(path)).SelectMany(entry => entry.Children))
        {
            if (!int.TryParse(entry.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var index)) continue;
            var value = entry.Value ?? entry.Children.FirstOrDefault(child => child.Key.Equals("path", StringComparison.OrdinalIgnoreCase))?.Value;
            if (!string.IsNullOrWhiteSpace(value)) yield return (index, Path.GetFullPath(value));
        }
    }

    private static Dictionary<string, string> ReadFields(string path) => ValveKeyValueParser.Parse(File.ReadAllText(path))
        .SelectMany(entry => entry.Children).Where(entry => entry.Value is not null)
        .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.Last().Value!, StringComparer.OrdinalIgnoreCase);

    private static SteamLibrary CreateLibrary(string root, int? index, SteamLibrarySource source)
    {
        var normalized = Path.GetFullPath(root);
        return new SteamLibrary(normalized, Directory.Exists(normalized) ? normalized : null, Path.Combine(normalized, "steamapps"), Path.Combine(normalized, "steamapps", "common"), Path.Combine(normalized, "steamapps", "workshop"), Path.Combine(normalized, "steamapps", "compatdata"), index, source);
    }

    private static bool IsSafeDirectoryName(string value) => !string.IsNullOrWhiteSpace(value) && !Path.IsPathRooted(value) && !value.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("..");
    private static DiscoveryDiagnostic Diagnostic(string code, ErrorKind kind, string message, SteamAppId appId, string path) => new(code, kind, message, new Dictionary<string, string> { ["appId"] = appId.Value.ToString(CultureInfo.InvariantCulture), ["path"] = path });
    private static Result<SteamGameDiscovery> Failure(string code, string message, ErrorKind kind) => Result<SteamGameDiscovery>.Failure(new Error(code, message, kind));
}
