using AAML.Application.Common;
using AAML.Application.Configurations;
using AAML.Domain.Games;
using AAML.Domain.Mods;

namespace AAML.Infrastructure.Common.Configurations;

public sealed class FilesystemConfigurationDocumentCatalog : IConfigurationDocumentCatalog
{
    public async Task<Result<IReadOnlyList<ConfigurationDocumentSummary>>> ListAsync(IReadOnlyList<ModInstallation> installations, GameVariant variant, CancellationToken cancellationToken)
    {
        try { return await Task.Run(() => List(installations, variant, cancellationToken), cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return Failure("configuration.catalog_cancelled", "Configuration discovery was cancelled.", ErrorKind.Cancelled); }
    }

    private static Result<IReadOnlyList<ConfigurationDocumentSummary>> List(IReadOnlyList<ModInstallation> installations, GameVariant variant, CancellationToken cancellationToken)
    {
        try
        {
            var results = new List<ConfigurationDocumentSummary>();
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var installation in installations.Where(mod => variant != GameVariant.XCom2 || !mod.RequiresWarOfTheChosen))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var config = Path.Combine(installation.Key.LocationIdentity, "Config");
                if (!Directory.Exists(config)) continue;
                foreach (var path in Directory.EnumerateFiles(config, "*", options).Where(path => Path.GetExtension(path).Equals(".ini", StringComparison.OrdinalIgnoreCase)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(installation.Key.LocationIdentity, path).Replace('\\', '/');
                    results.Add(new ConfigurationDocumentSummary(new ConfigurationDocumentId(installation.Key, relative), installation.Name, relative));
                }
            }
            return Result<IReadOnlyList<ConfigurationDocumentSummary>>.Success(results.OrderBy(item => item.ModName, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Id.Mod.LocationIdentity, StringComparer.Ordinal).ToArray());
        }
        catch (OperationCanceledException) { return Failure("configuration.catalog_cancelled", "Configuration discovery was cancelled.", ErrorKind.Cancelled); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { return Failure("configuration.catalog_failed", exception.Message, ErrorKind.Io); }
    }

    private static Result<IReadOnlyList<ConfigurationDocumentSummary>> Failure(string code, string message, ErrorKind kind) => Result<IReadOnlyList<ConfigurationDocumentSummary>>.Failure(new Error(code, message, kind));
}
