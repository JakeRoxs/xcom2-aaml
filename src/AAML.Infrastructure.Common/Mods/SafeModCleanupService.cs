using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AAML.Application.Common;
using AAML.Application.Mods.Cleanup;
using AAML.Application.Ports;
using AAML.Domain.Mods;

namespace AAML.Infrastructure.Common.Mods;

public sealed class SafeModCleanupService(IPathSemantics paths, TimeProvider timeProvider) : IModCleanupService
{
    private readonly ConcurrentDictionary<string, Plan> plans = new();

    public Task<Result<ModCleanupPreview>> PreviewAsync(ModCleanupRequest request, CancellationToken cancellationToken) => Task.Run(() => Preview(request, cancellationToken), cancellationToken);

    private Result<ModCleanupPreview> Preview(ModCleanupRequest request, CancellationToken cancellationToken)
    {
        if (request.SourcePolicy == SourceCleanupPolicy.None && request.ShaderPolicy == ShaderCleanupPolicy.None) return Result<ModCleanupPreview>.Failure(new Error("cleanup.policy_empty", "Select at least one cleanup policy.", ErrorKind.Validation));
        var targets = new List<Target>(); var items = new List<ModCleanupItemPreview>();
        foreach (var mod in request.Mods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (mod.Key.Source == ModSource.SteamWorkshop && !request.IncludeWorkshop) { AddSkipped(mod, CleanupDisposition.SkippedWorkshop, "Steam Workshop cleanup was not enabled."); continue; }
            var authorized = request.AuthorizedRoots.Any(root => IsStrictlyContained(mod.Key.LocationIdentity, root));
            if (!authorized || HasReparseInChain(mod.Key.LocationIdentity)) { AddSkipped(mod, CleanupDisposition.Rejected, "The mod is outside an authorized root or traverses a reparse point."); continue; }
            if (request.SourcePolicy != SourceCleanupPolicy.None)
            {
                var kind = request.SourcePolicy == SourceCleanupPolicy.AllSource ? CleanupArtifactKind.SourceDirectory : CleanupArtifactKind.XComGameSourceDirectory;
                var relative = request.SourcePolicy == SourceCleanupPolicy.AllSource ? "src" : "src/XComGame";
                if (Directory.Exists(Path.Combine(mod.Key.LocationIdentity, "CookedPCConsole"))) items.Add(Item(mod, kind, relative, CleanupDisposition.SkippedCookedMod, "Source retained because CookedPCConsole exists.", null));
                else Inspect(mod, kind, relative, true);
            }
            if (request.ShaderPolicy != ShaderCleanupPolicy.None)
            {
                if (!ValidComponent(mod.PackageId.Value)) items.Add(Item(mod, CleanupArtifactKind.ModShaderCache, "Content", CleanupDisposition.Rejected, "Package ID is not a safe filename component.", null));
                else
                {
                    var relative = $"Content/{mod.PackageId.Value}_ModShaderCache.upk";
                    var full = Combine(mod.Key.LocationIdentity, relative);
                    if (request.ShaderPolicy == ShaderCleanupPolicy.EmptyLegacyCacheOnly && File.Exists(full) && new FileInfo(full).Length != 371) items.Add(Item(mod, CleanupArtifactKind.ModShaderCache, relative, CleanupDisposition.NotFound, "Shader cache is not the 371-byte legacy empty cache.", null));
                    else Inspect(mod, CleanupArtifactKind.ModShaderCache, relative, false);
                }
            }
        }
        var revision = Revision(targets);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var expiry = timeProvider.GetUtcNow().AddMinutes(10);
        plans[token] = new Plan(revision, expiry, targets);
        return Result<ModCleanupPreview>.Success(new(token, revision, expiry, items));

        void AddSkipped(ModInstallation mod, CleanupDisposition disposition, string message)
        {
            if (request.SourcePolicy != SourceCleanupPolicy.None) items.Add(Item(mod, request.SourcePolicy == SourceCleanupPolicy.AllSource ? CleanupArtifactKind.SourceDirectory : CleanupArtifactKind.XComGameSourceDirectory, request.SourcePolicy == SourceCleanupPolicy.AllSource ? "src" : "src/XComGame", disposition, message, null));
            if (request.ShaderPolicy != ShaderCleanupPolicy.None) items.Add(Item(mod, CleanupArtifactKind.ModShaderCache, "Content", disposition, message, null));
        }
        void Inspect(ModInstallation mod, CleanupArtifactKind kind, string relative, bool directory)
        {
            var full = Combine(mod.Key.LocationIdentity, relative);
            if (directory ? !Directory.Exists(full) : !File.Exists(full)) { items.Add(Item(mod, kind, relative, CleanupDisposition.NotFound, "Target does not exist.", null)); return; }
            try
            {
                var manifest = Manifest(full, directory, cancellationToken);
                if (manifest.Reparse) { items.Add(Item(mod, kind, relative, CleanupDisposition.Rejected, "Target contains a reparse point.", null)); return; }
                var target = new Target(Guid.NewGuid().ToString("N"), mod.Key, full, kind, manifest.Revision); targets.Add(target);
                items.Add(Item(mod, kind, relative, CleanupDisposition.Ready, "Ready for confirmation.", (manifest.Files, manifest.Directories, manifest.Bytes), target.ItemId));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { items.Add(Item(mod, kind, relative, CleanupDisposition.Unreadable, exception.Message, null)); }
        }
    }

    public async Task<Result<ModCleanupExecutionResult>> ExecuteAsync(ModCleanupPreview preview, CancellationToken cancellationToken)
    {
        if (!plans.TryRemove(preview.ConfirmationToken, out var plan) || plan.Revision != preview.Revision) return Result<ModCleanupExecutionResult>.Failure(new Error("cleanup.confirmation_invalid", "Cleanup confirmation is invalid or was already used.", ErrorKind.Conflict));
        if (timeProvider.GetUtcNow() > plan.ExpiresAt) return Result<ModCleanupExecutionResult>.Failure(new Error("cleanup.confirmation_expired", "Cleanup preview expired.", ErrorKind.Conflict));
        var results = new List<ModCleanupItemResult>();
        foreach (var target in plan.Targets)
        {
            if (cancellationToken.IsCancellationRequested) { results.Add(new(target.ItemId, CleanupItemOutcome.Cancelled, "Cancellation stopped this item.")); continue; }
            try
            {
                var directory = target.Kind != CleanupArtifactKind.ModShaderCache;
                if (directory ? !Directory.Exists(target.Path) : !File.Exists(target.Path)) { results.Add(new(target.ItemId, CleanupItemOutcome.ChangedSincePreview, "Target no longer exists.")); continue; }
                var current = Manifest(target.Path, directory, cancellationToken);
                if (current.Reparse || current.Revision != target.Revision) { results.Add(new(target.ItemId, CleanupItemOutcome.ChangedSincePreview, "Target changed after preview.")); continue; }
                if (directory) DeleteTree(target.Path, cancellationToken); else File.Delete(target.Path);
                results.Add(new(target.ItemId, CleanupItemOutcome.Deleted, "Deleted."));
            }
            catch (OperationCanceledException) { results.Add(new(target.ItemId, CleanupItemOutcome.Cancelled, "Cancellation stopped this item.")); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { results.Add(new(target.ItemId, CleanupItemOutcome.Failed, exception.Message)); }
        }
        await Task.CompletedTask;
        return Result<ModCleanupExecutionResult>.Success(new(results, cancellationToken.IsCancellationRequested));
    }

    private bool IsStrictlyContained(string candidate, string root) { var contained = paths.IsContainedBy(candidate, root); return contained.IsSuccess && contained.Value && !paths.AreEqual(candidate, root); }
    private static string Combine(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static bool ValidComponent(string value) => !string.IsNullOrWhiteSpace(value) && value is not "." and not ".." && value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '\0', ':']) < 0;
    private static bool HasReparseInChain(string path) { for (var current = new DirectoryInfo(path); current is not null; current = current.Parent) if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true; return false; }
    private static ManifestInfo Manifest(string target, bool directory, CancellationToken cancellationToken)
    {
        var entries = directory ? EnumerateNoFollow(target, cancellationToken).Order(StringComparer.Ordinal).ToArray() : [target];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var files = 0; var directories = 0; long bytes = 0; var reparse = false;
        foreach (var entry in entries) { cancellationToken.ThrowIfCancellationRequested(); var attributes = File.GetAttributes(entry); reparse |= attributes.HasFlag(FileAttributes.ReparsePoint); hash.AppendData(Encoding.UTF8.GetBytes(entry)); if (attributes.HasFlag(FileAttributes.Directory)) directories++; else { files++; var data = File.ReadAllBytes(entry); bytes += data.Length; hash.AppendData(data); } }
        return new(Convert.ToHexString(hash.GetHashAndReset()), files, directories, bytes, reparse);
    }
    private static IEnumerable<string> EnumerateNoFollow(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop(); yield return current;
            var attributes = File.GetAttributes(current);
            if (!attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            foreach (var child in Directory.EnumerateFileSystemEntries(current)) pending.Push(child);
        }
    }
    private static void DeleteTree(string target, CancellationToken cancellationToken)
    {
        foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)) { cancellationToken.ThrowIfCancellationRequested(); if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("A reparse point appeared during cleanup."); File.Delete(file); }
        foreach (var directory in Directory.EnumerateDirectories(target, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length)) { cancellationToken.ThrowIfCancellationRequested(); if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("A reparse point appeared during cleanup."); Directory.Delete(directory); }
        Directory.Delete(target);
    }
    private static string Revision(IReadOnlyList<Target> targets) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', targets.Select(target => $"{target.Mod}|{target.Kind}|{target.Path}|{target.Revision}")))));
    private static ModCleanupItemPreview Item(ModInstallation mod, CleanupArtifactKind kind, string relative, CleanupDisposition disposition, string message, (int Files, int Directories, long Bytes)? counts, string? itemId = null) => new(itemId ?? Guid.NewGuid().ToString("N"), mod.Key, mod.Name, kind, relative, disposition, message, counts?.Files ?? 0, counts?.Directories ?? 0, counts?.Bytes ?? 0);
    private sealed record Plan(string Revision, DateTimeOffset ExpiresAt, IReadOnlyList<Target> Targets);
    private sealed record Target(string ItemId, ModKey Mod, string Path, CleanupArtifactKind Kind, string Revision);
    private sealed record ManifestInfo(string Revision, int Files, int Directories, long Bytes, bool Reparse);
}
