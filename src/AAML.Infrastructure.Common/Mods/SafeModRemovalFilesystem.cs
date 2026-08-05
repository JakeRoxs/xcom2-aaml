using System.Collections.Concurrent;
using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Domain.Mods;

namespace AAML.Infrastructure.Common.Mods;

public sealed class SafeModRemovalFilesystem(IPathSemantics paths) : IModRemovalFilesystem
{
    private readonly ConcurrentDictionary<string, (ModKey Mod, string Fingerprint)> previews = [];

    public Task<Result<ModRemovalPreview>> PreviewAsync(ModKey mod, IReadOnlyList<string> configuredRoots, CancellationToken cancellationToken) => Task.Run(() =>
    {
        try
        {
            if (mod.Source != ModSource.Manual) return Result<ModRemovalPreview>.Failure(new Error("removal.workshop_forbidden", "Workshop content must be removed through Steam unsubscribe.", ErrorKind.Validation));
            if (!Directory.Exists(mod.LocationIdentity)) return Result<ModRemovalPreview>.Failure(new Error("removal.not_found", "The mod directory no longer exists.", ErrorKind.NotFound));
            var contained = configuredRoots.Any(root => paths.AreEqual(mod.LocationIdentity, root) || paths.IsContainedBy(mod.LocationIdentity, root).Value == true && !paths.AreEqual(mod.LocationIdentity, root));
            if (!contained || configuredRoots.Any(root => paths.AreEqual(mod.LocationIdentity, root))) return Result<ModRemovalPreview>.Failure(new Error("removal.outside_roots", "The mod directory must be a child of a configured root and cannot be the root itself.", ErrorKind.Validation));
            var files = EnumerateSafe(mod.LocationIdentity);
            var token = Guid.NewGuid().ToString("N"); previews[token] = (mod, Fingerprint(files));
            return Result<ModRemovalPreview>.Success(new ModRemovalPreview(token, mod, files.Length, files.Sum(file => new FileInfo(file).Length), files.Take(10).Select(file => Path.GetRelativePath(mod.LocationIdentity, file)).ToArray()));
        }
        catch (OperationCanceledException) { return Result<ModRemovalPreview>.Failure(new Error("removal.cancelled", "Removal preview was cancelled.", ErrorKind.Cancelled)); }
        catch (InvalidDataException exception) { return Result<ModRemovalPreview>.Failure(new Error("removal.reparse_point", exception.Message, ErrorKind.Validation)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { return Result<ModRemovalPreview>.Failure(new Error("removal.preview_failed", exception.Message, ErrorKind.Io)); }
    }, cancellationToken);

    public Task<Result> DeleteConfirmedAsync(ModRemovalPreview preview, CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (!previews.TryRemove(preview.ConfirmationToken, out var stored) || stored.Mod != preview.Mod) return Result.Failure(new Error("removal.confirmation_invalid", "Removal confirmation expired or does not match the mod.", ErrorKind.Conflict));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(preview.Mod.LocationIdentity)) return Result.Failure(new Error("removal.not_found", "The mod directory no longer exists.", ErrorKind.NotFound));
            var files = EnumerateSafe(preview.Mod.LocationIdentity);
            if (Fingerprint(files) != stored.Fingerprint) return Result.Failure(new Error("removal.changed", "The mod directory changed after preview; preview again.", ErrorKind.Conflict));
            Directory.Delete(preview.Mod.LocationIdentity, true); return Result.Success();
        }
        catch (OperationCanceledException) { return Result.Failure(new Error("removal.cancelled", "Removal was cancelled.", ErrorKind.Cancelled)); }
        catch (InvalidDataException exception) { return Result.Failure(new Error("removal.reparse_point", exception.Message, ErrorKind.Validation)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return Result.Failure(new Error("removal.delete_failed", exception.Message, ErrorKind.Io)); }
    }, cancellationToken);

    private static string Fingerprint(IEnumerable<string> files)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var file in files) { hash.AppendData(System.Text.Encoding.UTF8.GetBytes(file)); hash.AppendData(File.ReadAllBytes(file)); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string[] EnumerateSafe(string root)
    {
        var files = new List<string>(); var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0) foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()).Order(StringComparer.Ordinal)) { var attributes = File.GetAttributes(entry); if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Deletion refused a link or reparse point."); if (attributes.HasFlag(FileAttributes.Directory)) pending.Push(entry); else files.Add(entry); }
        return files.Order(StringComparer.Ordinal).ToArray();
    }
}
