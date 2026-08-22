using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Application.Steam;
using AAML.Domain.Games;
using AAML.Infrastructure.Linux.Launching;

namespace AAML.Infrastructure.Linux.Steam;

/// <summary>Uses production Steam discovery and layout resolution to produce read-only Linux diagnostics.</summary>
public sealed class LinuxEnvironmentDiagnosticService(ISteamFilesystemDiscovery discovery) : ILinuxEnvironmentDiagnosticService
{
    public async Task<Result<LinuxEnvironmentDiagnostic>> InspectAsync(LinuxEnvironmentDiagnosticRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!OperatingSystem.IsLinux())
            return Failure("linux_environment.platform_unsupported", "Linux environment diagnostics require Linux.", ErrorKind.Unavailable);
        if (request.Variants.Count == 0)
            return Failure("linux_environment.variants_required", "At least one supported game variant is required.", ErrorKind.Validation);
        if (request.Variants.Any(variant => variant is not (GameVariant.XCom2 or GameVariant.XCom2WarOfTheChosen)))
            return Failure("linux_environment.variant_unsupported", "Linux diagnostics support XCom2 and XCom2WarOfTheChosen only.", ErrorKind.Validation);

        var discovered = await discovery.DiscoverAsync(new SteamDiscoveryRequest([SteamAppId.Xcom2], request.CandidateSteamRoots), cancellationToken).ConfigureAwait(false);
        if (!discovered.IsSuccess) return Result<LinuxEnvironmentDiagnostic>.Failure(discovered.Error!);
        var selectedInstallation = SelectInstallation(discovered.Value!, request.InstallationLocation);
        if (!selectedInstallation.IsSuccess) return Result<LinuxEnvironmentDiagnostic>.Failure(selectedInstallation.Error!);

        cancellationToken.ThrowIfCancellationRequested();
        var variants = new List<LinuxGameVariantDiagnostic>();
        foreach (var variant in request.Variants.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            variants.Add(InspectVariant(selectedInstallation.Value!, variant));
        }
        var report = new LinuxEnvironmentDiagnostic(
            1,
            variants.All(variant => variant.Success),
            SteamAppId.Xcom2.Value,
            selectedInstallation.Value,
            discovered.Value!.WorkshopLocations.Where(location => Directory.Exists(location.ContentRootPath)).Select(location => location.ContentRootPath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            discovered.Value.ProtonPrefixes.Where(prefix => prefix.Exists).Select(prefix => prefix.PhysicalPrefixPath ?? prefix.PrefixPath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            variants,
            discovered.Value.Diagnostics.Select(diagnostic => new LinuxDiscoveryDiagnostic(diagnostic.Code, diagnostic.Kind.ToString(), diagnostic.Message, diagnostic.Metadata, diagnostic.IsWarning)).ToArray());
        return Result<LinuxEnvironmentDiagnostic>.Success(report);
    }

    private static Result<string> SelectInstallation(SteamGameDiscovery discovery, string? explicitInstallation)
    {
        if (!string.IsNullOrWhiteSpace(explicitInstallation))
        {
            if (!Directory.Exists(explicitInstallation)) return Result<string>.Failure(new Error("linux_environment.installation_missing", $"Installation directory does not exist: {explicitInstallation}", ErrorKind.NotFound));
            return Result<string>.Success(Path.GetFullPath(explicitInstallation));
        }

        var installed = discovery.Applications.Where(application => application.InstallDirectoryExists)
            .Select(application => application.GameInstallPath).Distinct(StringComparer.Ordinal).ToArray();
        return installed.Length switch
        {
            1 => Result<string>.Success(installed[0]),
            0 => Result<string>.Failure(new Error("linux_environment.installation_missing", "Steam did not identify an installed XCOM 2 copy.", ErrorKind.NotFound)),
            _ => Result<string>.Failure(new Error("linux_environment.installation_ambiguous", $"Steam identified multiple installed XCOM 2 copies: {string.Join(", ", installed.Order(StringComparer.Ordinal))}", ErrorKind.Conflict))
        };
    }

    private static LinuxGameVariantDiagnostic InspectVariant(string installation, GameVariant variant)
    {
        var layout = LinuxSteamGameLayout.Resolve(installation, variant);
        if (!layout.IsSuccess)
            return new(variant.ToString(), false, layout.Error!.Code, layout.Error.Message, installation, null, null, null, null, null, false, null, false, []);
        var value = layout.Value!;
        return new(
            variant.ToString(),
            true,
            null,
            null,
            value.GameInstallPath,
            value.TargetExecutablePath,
            value.SteamAppsPath,
            value.PrefixPath,
            value.WineUser,
            value.UserDataDirectory,
            Directory.Exists(value.UserDataDirectory),
            value.ConfigurationDirectory,
            Directory.Exists(value.ConfigurationDirectory),
            value.CaseFallbacks.Select(fallback => new LinuxCaseFallbackDiagnostic(fallback.ExpectedPath, fallback.ActualPath)).ToArray());
    }

    private static Result<LinuxEnvironmentDiagnostic> Failure(string code, string message, ErrorKind kind) =>
        Result<LinuxEnvironmentDiagnostic>.Failure(new Error(code, message, kind));
}
