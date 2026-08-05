using AAML.Application.Common;

namespace AAML.Application.Steam;

public readonly record struct SteamAppId(uint Value)
{
    public static SteamAppId Xcom2 => new(268500);
    public static SteamAppId ChimeraSquad => new(882100);
}

public sealed record SteamDiscoveryRequest(IReadOnlyList<SteamAppId> AppIds, IReadOnlyList<string>? CandidateSteamRoots = null);
public enum SteamInstallationKind { Native, Flatpak, Explicit }
public enum SteamLibrarySource { Root, LibraryFoldersVdf, Explicit }

public sealed record SteamInstallation(string RootPath, string? PhysicalRootPath, SteamInstallationKind Kind, string SteamAppsPath, string? UserDataPath);
public sealed record SteamLibrary(string RootPath, string? PhysicalRootPath, string SteamAppsPath, string CommonPath, string WorkshopPath, string CompatDataPath, int? DeclaredIndex, SteamLibrarySource Source);
public sealed record SteamInstalledApplication(SteamAppId AppId, string LibraryRootPath, string ManifestPath, string InstallDirectoryName, string GameInstallPath, string? Name, string? StateFlags, bool ManifestExists, bool InstallDirectoryExists);
public sealed record SteamWorkshopLocation(SteamAppId AppId, string LibraryRootPath, string ContentRootPath, IReadOnlyList<string> ExistingItemDirectories);
public sealed record ProtonPrefix(SteamAppId AppId, string LibraryRootPath, string CompatDataPath, string PrefixPath, string? PhysicalPrefixPath, bool Exists, bool HasDriveC, IReadOnlyList<string> WineUsers);
public sealed record SteamUserDataLocation(string RootPath, IReadOnlyList<string> UserDirectories, IReadOnlyList<string> AppDataPaths);
public sealed record DiscoveryDiagnostic(string Code, ErrorKind Kind, string Message, IReadOnlyDictionary<string, string>? Metadata = null, bool IsWarning = false);
public sealed record SteamGameDiscovery(SteamAppId AppId, IReadOnlyList<SteamInstallation> Installations, IReadOnlyList<SteamLibrary> Libraries, IReadOnlyList<SteamInstalledApplication> Applications, IReadOnlyList<SteamWorkshopLocation> WorkshopLocations, IReadOnlyList<ProtonPrefix> ProtonPrefixes, IReadOnlyList<SteamUserDataLocation> UserDataLocations, IReadOnlyList<DiscoveryDiagnostic> Diagnostics);

public interface ISteamFilesystemDiscovery
{
    Task<Result<SteamGameDiscovery>> DiscoverAsync(SteamDiscoveryRequest request, CancellationToken cancellationToken);
}
