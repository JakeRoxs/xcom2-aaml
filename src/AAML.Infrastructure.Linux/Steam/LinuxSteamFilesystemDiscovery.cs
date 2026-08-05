using System.Globalization;
using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Application.Steam;
using AAML.Infrastructure.Common.Steam;
using AAML.Infrastructure.Linux.Paths;

namespace AAML.Infrastructure.Linux.Steam;

public sealed class LinuxSteamFilesystemDiscovery : ISteamFilesystemDiscovery
{
    private readonly LinuxPathSemantics semantics = new();
    private readonly IPhysicalPathResolver physical;

    public LinuxSteamFilesystemDiscovery(IPhysicalPathResolver physical) => this.physical = physical;

    public Task<Result<SteamGameDiscovery>> DiscoverAsync(SteamDiscoveryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!OperatingSystem.IsLinux()) return Task.FromResult(Failure("steam.discovery_platform_unsupported", "Linux filesystem discovery requires Linux.", ErrorKind.Unavailable));
        if (request.AppIds is null || request.AppIds.Count != 1 || request.AppIds.Any(id => id.Value == 0)) return Task.FromResult(Failure("steam.app_id_invalid", "Exactly one nonzero Steam app ID is required.", ErrorKind.Validation));

        try
        {
            var roots = CandidateRoots(request.CandidateSteamRoots).Distinct(StringComparer.Ordinal).ToArray();
            var installations = roots.Where(Directory.Exists).Select(CreateInstallation).ToArray();
            if (installations.Length == 0)
                return Task.FromResult(Failure("steam.no_installation_found", "No Steam installation was found.", ErrorKind.NotFound));
            var libraries = new List<SteamLibrary>();
            foreach (var installation in installations)
            {
                libraries.Add(CreateLibrary(installation.RootPath, null, SteamLibrarySource.Root));
                var vdf = Path.Combine(installation.SteamAppsPath, "libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                foreach (var entry in ReadLibraries(vdf)) libraries.Add(CreateLibrary(entry.Path, entry.Index, SteamLibrarySource.LibraryFoldersVdf));
            }

            var uniqueLibraries = libraries.GroupBy(library => library.PhysicalRootPath ?? library.RootPath, StringComparer.Ordinal).Select(group => group.OrderBy(library => library.Source).First()).ToArray();
            var results = request.AppIds.Select(appId => DiscoverApp(appId, installations, uniqueLibraries, cancellationToken)).ToArray();
            return Task.FromResult(Merge(results));
        }
        catch (OperationCanceledException) { return Task.FromResult(Failure("steam.discovery_cancelled", "Steam discovery was cancelled.", ErrorKind.Cancelled)); }
        catch (FormatException exception) { return Task.FromResult(Failure("steam.library_file_invalid", exception.Message, ErrorKind.InvalidData)); }
        catch (IOException exception) { return Task.FromResult(Failure("steam.discovery_io", exception.Message, ErrorKind.Io)); }
    }

    private SteamGameDiscovery DiscoverApp(SteamAppId appId, IReadOnlyList<SteamInstallation> installations, IReadOnlyList<SteamLibrary> libraries, CancellationToken cancellationToken)
    {
        var applications = new List<SteamInstalledApplication>();
        var workshops = new List<SteamWorkshopLocation>();
        var prefixes = new List<ProtonPrefix>();
        var diagnostics = new List<DiscoveryDiagnostic>();
        foreach (var library in libraries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = Path.Combine(library.SteamAppsPath, $"appmanifest_{appId.Value}.acf");
            if (!File.Exists(manifest)) continue;
            var fields = ReadFields(manifest);
            if (fields.TryGetValue("appid", out var declared) && declared != appId.Value.ToString(CultureInfo.InvariantCulture))
            {
                diagnostics.Add(new DiscoveryDiagnostic("steam.manifest_app_id_mismatch", ErrorKind.InvalidData, "Manifest app ID does not match its filename", new Dictionary<string, string> { ["appId"] = appId.Value.ToString(CultureInfo.InvariantCulture), ["path"] = manifest }));
                continue;
            }
            if (!fields.TryGetValue("installdir", out var installDirectory) || string.IsNullOrWhiteSpace(installDirectory))
            {
                diagnostics.Add(new DiscoveryDiagnostic("steam.install_dir_missing", ErrorKind.InvalidData, "Manifest has no install directory", new Dictionary<string, string> { ["path"] = manifest }));
                continue;
            }
            if (Path.IsPathRooted(installDirectory) || installDirectory.Contains("..", StringComparison.Ordinal))
            {
                diagnostics.Add(new DiscoveryDiagnostic("steam.install_dir_invalid", ErrorKind.InvalidData, "Manifest install directory is not a safe relative path", new Dictionary<string, string> { ["path"] = manifest }));
                continue;
            }
            var gamePath = Path.Combine(library.CommonPath, installDirectory);
            applications.Add(new SteamInstalledApplication(appId, library.RootPath, manifest, installDirectory, gamePath, fields.GetValueOrDefault("name"), fields.GetValueOrDefault("StateFlags"), true, Directory.Exists(gamePath)));
            var contentRoot = Path.Combine(library.WorkshopPath, "content", appId.Value.ToString(CultureInfo.InvariantCulture));
            workshops.Add(new SteamWorkshopLocation(appId, library.RootPath, contentRoot, Directory.Exists(contentRoot) ? Directory.EnumerateDirectories(contentRoot).Where(path => ulong.TryParse(Path.GetFileName(path), out _)).Order(StringComparer.Ordinal).ToArray() : []));
            var compat = Path.Combine(library.CompatDataPath, appId.Value.ToString(CultureInfo.InvariantCulture));
            var prefix = Path.Combine(compat, "pfx");
            var users = Directory.Exists(prefix) && Directory.Exists(Path.Combine(prefix, "drive_c", "users")) ? Directory.EnumerateDirectories(Path.Combine(prefix, "drive_c", "users")).Select(Path.GetFileName).Where(name => name is not null).Cast<string>().Order(StringComparer.Ordinal).ToArray() : [];
            prefixes.Add(new ProtonPrefix(appId, library.RootPath, compat, prefix, Directory.Exists(prefix) ? physical.ResolveExisting(prefix).Value : null, Directory.Exists(prefix), Directory.Exists(Path.Combine(prefix, "drive_c")), users));
        }
        var installed = applications.Where(application => application.InstallDirectoryExists).ToArray();
        if (installed.Length > 1)
            diagnostics.Add(new DiscoveryDiagnostic("steam.game_install_ambiguous", ErrorKind.Conflict, "More than one library contains an installed copy of the game", new Dictionary<string, string> { ["appId"] = appId.Value.ToString(CultureInfo.InvariantCulture) }));
        var existingPrefixes = prefixes.Where(prefix => prefix.Exists).ToArray();
        if (installed.Length > 0 && existingPrefixes.Length == 0)
            diagnostics.Add(new DiscoveryDiagnostic("steam.proton_prefix_missing", ErrorKind.NotFound, "The installed game has no Proton prefix", new Dictionary<string, string> { ["appId"] = appId.Value.ToString(CultureInfo.InvariantCulture) }));
        if (existingPrefixes.Length > 1)
            diagnostics.Add(new DiscoveryDiagnostic("steam.proton_prefix_ambiguous", ErrorKind.Conflict, "More than one library contains a Proton prefix for the game", new Dictionary<string, string> { ["appId"] = appId.Value.ToString(CultureInfo.InvariantCulture) }));
        return new SteamGameDiscovery(appId, installations, libraries, applications, workshops, prefixes, [], diagnostics);
    }

    private SteamInstallation CreateInstallation(string root)
    {
        var physicalRoot = physical.ResolveExisting(root).IsSuccess ? physical.ResolveExisting(root).Value : null;
        var kind = root.Contains(".var/app/com.valvesoftware.Steam", StringComparison.Ordinal) ? SteamInstallationKind.Flatpak : SteamInstallationKind.Native;
        return new SteamInstallation(root, physicalRoot, kind, Path.Combine(root, "steamapps"), Path.Combine(root, "userdata"));
    }

    private SteamLibrary CreateLibrary(string root, int? index, SteamLibrarySource source)
    {
        var normalized = semantics.NormalizeIdentity(root).Value!;
        var resolved = physical.ResolveExisting(normalized);
        return new SteamLibrary(normalized, resolved.IsSuccess ? resolved.Value : null, normalized + "/steamapps", normalized + "/steamapps/common", normalized + "/steamapps/workshop", normalized + "/steamapps/compatdata", index, source);
    }

    private IEnumerable<(int Index, string Path)> ReadLibraries(string path)
    {
        foreach (var entry in ValveKeyValueParser.Parse(File.ReadAllText(path)).SelectMany(entry => entry.Children))
        {
            if (!int.TryParse(entry.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var index)) continue;
            var value = entry.Value ?? entry.Children.FirstOrDefault(child => child.Key == "path")?.Value;
            if (!string.IsNullOrWhiteSpace(value) && semantics.NormalizeIdentity(value).IsSuccess) yield return (index, semantics.NormalizeIdentity(value).Value!);
        }
    }

    private Dictionary<string, string> ReadFields(string path) => ValveKeyValueParser.Parse(File.ReadAllText(path)).SelectMany(entry => entry.Children).Where(entry => entry.Value is not null).GroupBy(entry => entry.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Last().Value!, StringComparer.Ordinal);

    private IEnumerable<string> CandidateRoots(IReadOnlyList<string>? explicitRoots)
    {
        if (explicitRoots is not null) foreach (var root in explicitRoots) if (semantics.NormalizeIdentity(root).IsSuccess) yield return semantics.NormalizeIdentity(root).Value!;
        if (explicitRoots is not null && explicitRoots.Count > 0) yield break;

        var home = Environment.GetEnvironmentVariable("HOME");
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(home) || !semantics.NormalizeIdentity(home).IsSuccess) yield break;
        if (!string.IsNullOrWhiteSpace(data) && semantics.NormalizeIdentity(data).IsSuccess)
        {
            yield return data.TrimEnd('/') + "/Steam";
            yield return data.TrimEnd('/') + "/steam";
        }
        yield return home.TrimEnd('/') + "/.local/share/Steam";
        yield return home.TrimEnd('/') + "/.steam/root";
        yield return home.TrimEnd('/') + "/.steam/steam";
        yield return home.TrimEnd('/') + "/.var/app/com.valvesoftware.Steam/.local/share/Steam";
        yield return home.TrimEnd('/') + "/.var/app/com.valvesoftware.Steam/.steam/root";
    }

    private Result<SteamGameDiscovery> Merge(IReadOnlyList<SteamGameDiscovery> discoveries) => Result<SteamGameDiscovery>.Success(discoveries.First());
    private static Result<SteamGameDiscovery> Failure(string code, string message, ErrorKind kind) => Result<SteamGameDiscovery>.Failure(new Error(code, message, kind));
}
