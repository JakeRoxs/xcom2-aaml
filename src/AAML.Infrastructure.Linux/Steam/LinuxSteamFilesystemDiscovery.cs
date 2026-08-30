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
    private readonly LinuxKnownArtifactResolver knownArtifacts;

    public LinuxSteamFilesystemDiscovery(IPhysicalPathResolver physical)
    {
        this.physical = physical;
        knownArtifacts = new LinuxKnownArtifactResolver(physical);
    }

    public Task<Result<SteamGameDiscovery>> DiscoverAsync(SteamDiscoveryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!OperatingSystem.IsLinux()) return Task.FromResult(Failure("steam.discovery_platform_unsupported", "Linux filesystem discovery requires Linux.", ErrorKind.Unavailable));
        if (request.AppIds is null || request.AppIds.Count != 1 || request.AppIds.Any(id => id.Value == 0)) return Task.FromResult(Failure("steam.app_id_invalid", "Exactly one nonzero Steam app ID is required.", ErrorKind.Validation));

        try
        {
            var roots = CandidateRoots(request.CandidateSteamRoots).Distinct(StringComparer.Ordinal).ToArray();
            var installations = new List<SteamInstallation>();
            foreach (var root in roots.Where(Directory.Exists))
            {
                var installation = CreateInstallation(root);
                if (!installation.IsSuccess) return Task.FromResult(Failure(installation.Error!.Code, installation.Error.Message, installation.Error.Kind));
                installations.Add(installation.Value!);
            }
            if (installations.Count == 0)
                return Task.FromResult(Failure("steam.no_installation_found", "No Steam installation was found.", ErrorKind.NotFound));
            var libraries = new List<SteamLibrary>();
            foreach (var installation in installations)
            {
                var rootLibrary = CreateLibrary(installation.RootPath, null, SteamLibrarySource.Root);
                if (!rootLibrary.IsSuccess) return Task.FromResult(Failure(rootLibrary.Error!.Code, rootLibrary.Error.Message, rootLibrary.Error.Kind));
                libraries.Add(rootLibrary.Value!);
                var vdf = Path.Combine(installation.SteamAppsPath, "libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                foreach (var entry in ReadLibraries(vdf))
                {
                    var library = CreateLibrary(entry.Path, entry.Index, SteamLibrarySource.LibraryFoldersVdf);
                    if (!library.IsSuccess) return Task.FromResult(Failure(library.Error!.Code, library.Error.Message, library.Error.Kind));
                    libraries.Add(library.Value!);
                }
            }

            var uniqueLibraries = libraries.GroupBy(library => library.PhysicalRootPath ?? library.RootPath, StringComparer.Ordinal).Select(group => group.OrderBy(library => library.Source).First()).ToArray();
            var results = request.AppIds.Select(appId => DiscoverApp(appId, installations, uniqueLibraries, cancellationToken)).ToArray();
            return Task.FromResult(Merge(results));
        }
        catch (OperationCanceledException) { return Task.FromResult(Failure("steam.discovery_cancelled", "Steam discovery was cancelled.", ErrorKind.Cancelled)); }
        catch (FormatException exception) { return Task.FromResult(Failure("steam.library_file_invalid", exception.Message, ErrorKind.InvalidData)); }
        catch (UnauthorizedAccessException exception) { return Task.FromResult(Failure("steam.discovery_unauthorized", exception.Message, ErrorKind.Unauthorized)); }
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
            var expectedGamePath = Path.Combine(library.CommonPath, installDirectory);
            var installComponents = installDirectory.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (installComponents.Length == 0)
            {
                diagnostics.Add(new DiscoveryDiagnostic("steam.install_dir_invalid", ErrorKind.InvalidData, "Manifest install directory has no path components", new Dictionary<string, string> { ["path"] = manifest }));
                continue;
            }
            var resolvedGame = knownArtifacts.ResolveExistingDirectory(library.CommonPath, installComponents);
            var gamePath = resolvedGame.IsSuccess ? resolvedGame.Value!.Path : expectedGamePath;
            if (!resolvedGame.IsSuccess && resolvedGame.Error!.Code != "path.known_artifact_missing")
                diagnostics.Add(new DiscoveryDiagnostic(resolvedGame.Error.Code, resolvedGame.Error.Kind, resolvedGame.Error.Message,
                    new Dictionary<string, string> { ["appId"] = appId.Value.ToString(CultureInfo.InvariantCulture), ["path"] = expectedGamePath }));
            applications.Add(new SteamInstalledApplication(appId, library.RootPath, manifest, installDirectory, gamePath, fields.GetValueOrDefault("name"), fields.GetValueOrDefault("StateFlags"), true, resolvedGame.IsSuccess));
            var contentRoot = ResolveKnownDirectoryOrExpected(library.WorkshopPath, "content", appId.Value.ToString(CultureInfo.InvariantCulture));
            if (contentRoot.IsSuccess)
                workshops.Add(new SteamWorkshopLocation(appId, library.RootPath, contentRoot.Value!, Directory.Exists(contentRoot.Value) ? Directory.EnumerateDirectories(contentRoot.Value).Where(path => ulong.TryParse(Path.GetFileName(path), out _)).Order(StringComparer.Ordinal).ToArray() : []));
            else diagnostics.Add(ToDiagnostic(contentRoot.Error!, appId, library.WorkshopPath));
            var compat = ResolveKnownDirectoryOrExpected(library.CompatDataPath, appId.Value.ToString(CultureInfo.InvariantCulture));
            if (!compat.IsSuccess) { diagnostics.Add(ToDiagnostic(compat.Error!, appId, library.CompatDataPath)); continue; }
            var prefix = ResolveKnownDirectoryOrExpected(compat.Value!, "pfx");
            if (!prefix.IsSuccess) { diagnostics.Add(ToDiagnostic(prefix.Error!, appId, compat.Value!)); continue; }
            var driveC = ResolveKnownDirectoryOrExpected(prefix.Value!, "drive_c");
            if (!driveC.IsSuccess) { diagnostics.Add(ToDiagnostic(driveC.Error!, appId, prefix.Value!)); continue; }
            var usersPath = ResolveKnownDirectoryOrExpected(driveC.Value!, "users");
            if (!usersPath.IsSuccess) { diagnostics.Add(ToDiagnostic(usersPath.Error!, appId, driveC.Value!)); continue; }
            var users = Directory.Exists(usersPath.Value) ? Directory.EnumerateDirectories(usersPath.Value).Select(Path.GetFileName).Where(name => name is not null).Cast<string>().Order(StringComparer.Ordinal).ToArray() : [];
            prefixes.Add(new ProtonPrefix(appId, library.RootPath, compat.Value!, prefix.Value!, Directory.Exists(prefix.Value) ? physical.ResolveExisting(prefix.Value).Value : null, Directory.Exists(prefix.Value), Directory.Exists(driveC.Value), users));
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

    private Result<SteamInstallation> CreateInstallation(string root)
    {
        var physicalRoot = physical.ResolveExisting(root).IsSuccess ? physical.ResolveExisting(root).Value : null;
        var kind = root.Contains(".var/app/com.valvesoftware.Steam", StringComparison.Ordinal) ? SteamInstallationKind.Flatpak : SteamInstallationKind.Native;
        var steamApps = ResolveKnownDirectoryOrExpected(root, "steamapps");
        if (!steamApps.IsSuccess) return Result<SteamInstallation>.Failure(steamApps.Error!);
        var userData = ResolveKnownDirectoryOrExpected(root, "userdata");
        if (!userData.IsSuccess) return Result<SteamInstallation>.Failure(userData.Error!);
        return Result<SteamInstallation>.Success(new(root, physicalRoot, kind, steamApps.Value!, userData.Value));
    }

    private Result<SteamLibrary> CreateLibrary(string root, int? index, SteamLibrarySource source)
    {
        var normalized = semantics.NormalizeIdentity(root).Value!;
        var resolved = physical.ResolveExisting(normalized);
        var steamApps = ResolveKnownDirectoryOrExpected(normalized, "steamapps");
        if (!steamApps.IsSuccess) return Result<SteamLibrary>.Failure(steamApps.Error!);
        var common = ResolveKnownDirectoryOrExpected(steamApps.Value!, "common");
        var workshop = ResolveKnownDirectoryOrExpected(steamApps.Value!, "workshop");
        var compatData = ResolveKnownDirectoryOrExpected(steamApps.Value!, "compatdata");
        var error = new[] { common, workshop, compatData }.FirstOrDefault(result => !result.IsSuccess).Error;
        return error is not null
            ? Result<SteamLibrary>.Failure(error)
            : Result<SteamLibrary>.Success(new(normalized, resolved.IsSuccess ? resolved.Value : null, steamApps.Value!, common.Value!, workshop.Value!, compatData.Value!, index, source));
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

    private Result<string> ResolveKnownDirectoryOrExpected(string root, params string[] components)
    {
        var resolved = knownArtifacts.ResolveDirectoryExistingOrExpected(root, components);
        return resolved.IsSuccess ? Result<string>.Success(resolved.Value!.Path) : Result<string>.Failure(resolved.Error!);
    }

    private static DiscoveryDiagnostic ToDiagnostic(Error error, SteamAppId appId, string path) => new(error.Code, error.Kind, error.Message,
        new Dictionary<string, string> { ["appId"] = appId.Value.ToString(CultureInfo.InvariantCulture), ["path"] = path });

    private IEnumerable<string> CandidateRoots(IReadOnlyList<string>? explicitRoots)
    {
        if (explicitRoots is not null) foreach (var root in explicitRoots) if (semantics.NormalizeIdentity(root).IsSuccess) yield return semantics.NormalizeIdentity(root).Value!;
        if (explicitRoots is not null && explicitRoots.Count > 0) yield break;

        var homeAliases = EnumerateHomeAliases(Environment.GetEnvironmentVariable("HOME")).ToArray();
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(data) && semantics.NormalizeIdentity(data).IsSuccess)
        {
            yield return data.TrimEnd('/') + "/Steam";
            yield return data.TrimEnd('/') + "/steam";
        }

        foreach (var home in homeAliases)
        {
            yield return home + "/.local/share/Steam";
            yield return home + "/.steam/root";
            yield return home + "/.steam/steam";
            yield return home + "/.var/app/com.valvesoftware.Steam/.local/share/Steam";
            yield return home + "/.var/app/com.valvesoftware.Steam/.steam/root";
        }
    }

    private static IEnumerable<string> EnumerateHomeAliases(string? home)
    {
        if (string.IsNullOrWhiteSpace(home)) yield break;

        var semantics = new LinuxPathSemantics();
        var trimmed = home.TrimEnd('/');
        var candidates = new HashSet<string>(StringComparer.Ordinal)
        {
            trimmed
        };

        var swapped = SwapHomeSegments(trimmed);
        if (swapped is not null) candidates.Add(swapped);

        foreach (var candidate in candidates)
        {
            if (semantics.NormalizeIdentity(candidate).IsSuccess)
                yield return semantics.NormalizeIdentity(candidate).Value!;
        }
    }

    private static string? SwapHomeSegments(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return null;

        if (parts.Length >= 3 && parts[^3] == "var" && parts[^2] == "home")
            return "/" + string.Join('/', [.. parts[..^3], "home", parts[^1]]);

        if (parts[^2] == "home")
            return "/" + string.Join('/', [.. parts[..^2], "var", "home", parts[^1]]);

        return null;
    }

    private Result<SteamGameDiscovery> Merge(IReadOnlyList<SteamGameDiscovery> discoveries) => Result<SteamGameDiscovery>.Success(discoveries.First());
    private static Result<SteamGameDiscovery> Failure(string code, string message, ErrorKind kind) => Result<SteamGameDiscovery>.Failure(new Error(code, message, kind));
}
