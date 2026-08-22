using AAML.Application.Common;
using AAML.Domain.Games;

namespace AAML.Application.Ports;

/// <summary>Read-only request for Linux Steam and Proton environment diagnostics.</summary>
public sealed record LinuxEnvironmentDiagnosticRequest(
    IReadOnlyList<GameVariant> Variants,
    string? InstallationLocation = null,
    IReadOnlyList<string>? CandidateSteamRoots = null);

/// <summary>Stable, service-owned Linux environment diagnostic output.</summary>
public sealed record LinuxEnvironmentDiagnostic(
    int SchemaVersion,
    bool Success,
    uint AppId,
    string? SelectedInstallation,
    IReadOnlyList<string> WorkshopRoots,
    IReadOnlyList<string> ProtonPrefixes,
    IReadOnlyList<LinuxGameVariantDiagnostic> Variants,
    IReadOnlyList<LinuxDiscoveryDiagnostic> DiscoveryDiagnostics);

/// <summary>Resolved production paths and casing evidence for one game variant.</summary>
public sealed record LinuxGameVariantDiagnostic(
    string Variant,
    bool Success,
    string? ErrorCode,
    string? ErrorMessage,
    string? GameInstallPath,
    string? TargetExecutablePath,
    string? SteamAppsPath,
    string? PrefixPath,
    string? WineUser,
    string? UserDataDirectory,
    bool UserDataExists,
    string? ConfigurationDirectory,
    bool ConfigurationExists,
    IReadOnlyList<LinuxCaseFallbackDiagnostic> CaseFallbacks);

/// <summary>One exact path expectation resolved through a unique casing fallback.</summary>
public sealed record LinuxCaseFallbackDiagnostic(string ExpectedPath, string ActualPath);

/// <summary>One Steam filesystem discovery diagnostic suitable for stable JSON output.</summary>
public sealed record LinuxDiscoveryDiagnostic(string Code, string Kind, string Message, IReadOnlyDictionary<string, string>? Metadata, bool IsWarning);

/// <summary>Inspects Linux Steam/Proton state without writing settings, game files, or subscriptions.</summary>
public interface ILinuxEnvironmentDiagnosticService
{
    Task<Result<LinuxEnvironmentDiagnostic>> InspectAsync(LinuxEnvironmentDiagnosticRequest request, CancellationToken cancellationToken);
}
