using AAML.Application.Common;
using AAML.Domain.Mods;

namespace AAML.Application.Mods.Cleanup;

public enum SourceCleanupPolicy { None, XComGameOnly, AllSource }
public enum ShaderCleanupPolicy { None, EmptyLegacyCacheOnly, AllModShaderCaches }
public enum CleanupArtifactKind { SourceDirectory, XComGameSourceDirectory, ModShaderCache }
public enum CleanupDisposition { Ready, NotFound, SkippedCookedMod, SkippedWorkshop, Rejected, Unreadable }
public enum CleanupItemOutcome { Deleted, Skipped, ChangedSincePreview, Failed, Cancelled }

public sealed record ModCleanupRequest(IReadOnlyList<ModInstallation> Mods, SourceCleanupPolicy SourcePolicy, ShaderCleanupPolicy ShaderPolicy, bool IncludeWorkshop, IReadOnlyList<string> AuthorizedRoots);
public sealed record ModCleanupItemPreview(string ItemId, ModKey Mod, string ModName, CleanupArtifactKind Kind, string RelativePath, CleanupDisposition Disposition, string Message, int FileCount, int DirectoryCount, long TotalBytes);
public sealed record ModCleanupPreview(string ConfirmationToken, string Revision, DateTimeOffset ExpiresAt, IReadOnlyList<ModCleanupItemPreview> Items);
public sealed record ModCleanupItemResult(string ItemId, CleanupItemOutcome Outcome, string Message);
public sealed record ModCleanupExecutionResult(IReadOnlyList<ModCleanupItemResult> Items, bool WasCancelled);

public interface IModCleanupService
{
    Task<Result<ModCleanupPreview>> PreviewAsync(ModCleanupRequest request, CancellationToken cancellationToken);
    Task<Result<ModCleanupExecutionResult>> ExecuteAsync(ModCleanupPreview preview, CancellationToken cancellationToken);
}
