using AAML.Application.Common;
using AAML.Domain.Mods;

namespace AAML.Application.Mods.Conflicts;

public enum ModConflictKind { File, ClassOverride }
public enum ModOverrideKind { Class, UiScreenListener }

public sealed record ModFileFact(ModKey Mod, string RelativePath);
public sealed record ModOverrideFact(ModKey Mod, PackageId PackageId, ModOverrideKind Kind, string BaseClass, string ReplacementClass, string RelativePath, int LineNumber, string OriginalLine);
public sealed record ModContentManifest(ModKey Mod, PackageId PackageId, IReadOnlyList<ModFileFact> Files, IReadOnlyList<ModOverrideFact> Overrides);
public sealed record ModConflictFact(ModKey Mod, PackageId PackageId, string Detail, string RelativePath, int? LineNumber);
public sealed record ModConflict(string Key, ModConflictKind Kind, string Subject, IReadOnlyList<ModKey> Participants, IReadOnlyList<ModConflictFact> Facts);
public sealed record ModConflictReport(IReadOnlyList<ModConflict> Conflicts, IReadOnlySet<string> AffectedKeys);

public interface IModContentIndexer
{
    Task<Result<ModContentManifest>> IndexAsync(ModInstallation installation, CancellationToken cancellationToken);
}

public interface IModConflictService
{
    Task<Result<ModConflictReport>> AnalyzeAsync(IReadOnlyList<ModInstallation> installations, IReadOnlySet<ModKey> activeMods, CancellationToken cancellationToken);
    Task<Result<ModConflictReport>> SetActiveAsync(IReadOnlySet<ModKey> activeMods, CancellationToken cancellationToken);
    void InvalidateContent(ModKey mod);
}

/// <summary>Caches physical manifests and incrementally projects conflicts for the active set.</summary>
public sealed class ModConflictService(IModContentIndexer indexer) : IModConflictService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<ModKey, ModContentManifest> manifests = [];
    private readonly Dictionary<ModKey, ModInstallation> installations = [];
    private readonly Dictionary<string, IReadOnlyList<ModFileFact>> filesByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<ModOverrideFact>> overridesByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<ModKey, IReadOnlySet<string>> keysByMod = [];
    private readonly Dictionary<string, ModConflict> conflicts = new(StringComparer.Ordinal);
    private HashSet<ModKey> active = [];

    public async Task<Result<ModConflictReport>> AnalyzeAsync(IReadOnlyList<ModInstallation> installations, IReadOnlySet<ModKey> activeMods, CancellationToken cancellationToken)
    {
        try { await gate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return Cancelled(); }
        try
        {
            var installed = installations.Select(mod => mod.Key).ToHashSet();
            var nextManifests = manifests.Where(pair => installed.Contains(pair.Key)).ToDictionary();
            foreach (var removed in this.installations.Keys.Where(key => !installed.Contains(key)).ToArray()) this.installations.Remove(removed);
            foreach (var installation in installations) this.installations[installation.Key] = installation;
            foreach (var installation in installations.Where(mod => activeMods.Contains(mod.Key)).OrderBy(mod => mod.Key, ModKeyComparer.Instance))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (nextManifests.ContainsKey(installation.Key)) continue;
                var indexed = await indexer.IndexAsync(installation, cancellationToken).ConfigureAwait(false);
                if (!indexed.IsSuccess) return Result<ModConflictReport>.Failure(indexed.Error!);
                nextManifests[installation.Key] = indexed.Value!;
            }

            manifests.Clear();
            foreach (var pair in nextManifests) manifests[pair.Key] = pair.Value;
            RebuildIndexes();
            var nextActive = activeMods.ToHashSet();
            var nextConflicts = new Dictionary<string, ModConflict>(StringComparer.Ordinal);
            foreach (var key in filesByKey.Keys.Concat(overridesByKey.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Project(key, nextActive) is { } conflict) nextConflicts[key] = conflict;
            }
            active = nextActive;
            conflicts.Clear();
            foreach (var pair in nextConflicts) conflicts[pair.Key] = pair.Value;
            return Result<ModConflictReport>.Success(Report(filesByKey.Keys.Concat(overridesByKey.Keys).ToHashSet(StringComparer.Ordinal)));
        }
        catch (OperationCanceledException)
        {
            return Result<ModConflictReport>.Failure(new Error("conflicts.cancelled", "Conflict analysis was cancelled.", ErrorKind.Cancelled));
        }
        finally { gate.Release(); }
    }

    public async Task<Result<ModConflictReport>> SetActiveAsync(IReadOnlySet<ModKey> activeMods, CancellationToken cancellationToken)
    {
        try { await gate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return Cancelled(); }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var changed = active.SymmetricExcept(activeMods);
            var affected = changed.SelectMany(mod => keysByMod.GetValueOrDefault(mod) ?? new HashSet<string>()).ToHashSet(StringComparer.Ordinal);
            var indexed = false;
            var addedManifests = new Dictionary<ModKey, ModContentManifest>();
            foreach (var mod in changed.Where(activeMods.Contains).Order(ModKeyComparer.Instance))
            {
                if (manifests.ContainsKey(mod) || !installations.TryGetValue(mod, out var installation)) continue;
                var result = await indexer.IndexAsync(installation, cancellationToken).ConfigureAwait(false);
                if (!result.IsSuccess) return Result<ModConflictReport>.Failure(result.Error!);
                addedManifests[mod] = result.Value!;
                indexed = true;
            }
            if (indexed)
            {
                foreach (var pair in addedManifests) manifests[pair.Key] = pair.Value;
                RebuildIndexes();
                foreach (var mod in changed) affected.UnionWith(keysByMod.GetValueOrDefault(mod) ?? new HashSet<string>());
            }
            var nextActive = activeMods.ToHashSet();
            var nextConflicts = new Dictionary<string, ModConflict>(conflicts, StringComparer.Ordinal);
            foreach (var key in affected.Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Project(key, nextActive) is { } conflict) nextConflicts[key] = conflict;
                else nextConflicts.Remove(key);
            }
            active = nextActive;
            conflicts.Clear();
            foreach (var pair in nextConflicts) conflicts[pair.Key] = pair.Value;
            return Result<ModConflictReport>.Success(Report(affected));
        }
        catch (OperationCanceledException)
        {
            return Result<ModConflictReport>.Failure(new Error("conflicts.cancelled", "Conflict analysis was cancelled.", ErrorKind.Cancelled));
        }
        finally { gate.Release(); }
    }

    public void InvalidateContent(ModKey mod) => manifests.Remove(mod);

    private void RebuildIndexes()
    {
        filesByKey.Clear();
        overridesByKey.Clear();
        keysByMod.Clear();
        foreach (var manifest in manifests.Values)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in manifest.Files.GroupBy(fact => "file:" + Canonical(fact.RelativePath), StringComparer.Ordinal))
            {
                filesByKey[group.Key] = Append(filesByKey.GetValueOrDefault(group.Key), group, FileFactComparer.Instance);
                keys.Add(group.Key);
            }
            foreach (var group in manifest.Overrides.GroupBy(fact => "class:" + fact.BaseClass.ToUpperInvariant(), StringComparer.Ordinal))
            {
                overridesByKey[group.Key] = Append(overridesByKey.GetValueOrDefault(group.Key), group, OverrideFactComparer.Instance);
                keys.Add(group.Key);
            }
            keysByMod[manifest.Mod] = keys;
        }
    }

    private ModConflict? Project(string key, IReadOnlySet<ModKey> activeSet)
    {
        if (key.StartsWith("file:", StringComparison.Ordinal))
        {
            var facts = filesByKey.GetValueOrDefault(key)?.Where(fact => activeSet.Contains(fact.Mod)).ToArray() ?? [];
            var participants = Participants(facts.Select(fact => fact.Mod));
            if (participants.Count < 2) return null;
            return new ModConflict(key, ModConflictKind.File, facts.Select(fact => fact.RelativePath).Min(StringComparer.Ordinal)!, participants,
                facts.Select(fact => new ModConflictFact(fact.Mod, PackageFor(fact.Mod), "Same relative file", fact.RelativePath, null)).ToArray());
        }

        var overrides = overridesByKey.GetValueOrDefault(key)?.Where(fact => activeSet.Contains(fact.Mod)).ToArray() ?? [];
        var overrideParticipants = Participants(overrides.Select(fact => fact.Mod));
        if (overrideParticipants.Count < 2 || overrides.All(fact => fact.Kind != ModOverrideKind.Class)) return null;
        if (overrides.Select(fact => (fact.Kind, Replacement: fact.ReplacementClass.ToUpperInvariant())).Distinct().Count() < 2) return null;
        var subject = overrides.Select(fact => fact.BaseClass).Min(StringComparer.Ordinal)!;
        return new ModConflict(key, ModConflictKind.ClassOverride, subject, overrideParticipants,
            overrides.Select(fact => new ModConflictFact(fact.Mod, fact.PackageId, $"{fact.Kind}: {fact.ReplacementClass}", fact.RelativePath, fact.LineNumber)).ToArray());
    }

    private PackageId PackageFor(ModKey mod) => manifests[mod].PackageId;
    private static Result<ModConflictReport> Cancelled() => Result<ModConflictReport>.Failure(new Error("conflicts.cancelled", "Conflict analysis was cancelled.", ErrorKind.Cancelled));
    private ModConflictReport Report(IReadOnlySet<string> affected) => new(conflicts.Values.OrderBy(conflict => conflict.Kind).ThenBy(conflict => conflict.Subject, StringComparer.OrdinalIgnoreCase).ThenBy(conflict => conflict.Key, StringComparer.Ordinal).ToArray(), affected);
    private static IReadOnlyList<ModKey> Participants(IEnumerable<ModKey> mods) => mods.Distinct().Order(ModKeyComparer.Instance).ToArray();
    private static string Canonical(string path) => path.Replace('\\', '/').ToUpperInvariant();
    private static IReadOnlyList<T> Append<T>(IReadOnlyList<T>? existing, IEnumerable<T> added, IComparer<T> comparer) => (existing ?? []).Concat(added).Order(comparer).ToArray();

    private sealed class ModKeyComparer : IComparer<ModKey>
    {
        public static ModKeyComparer Instance { get; } = new();
        public int Compare(ModKey left, ModKey right) => left.Source != right.Source ? left.Source.CompareTo(right.Source) : StringComparer.Ordinal.Compare(left.LocationIdentity, right.LocationIdentity);
    }
    private sealed class FileFactComparer : IComparer<ModFileFact>
    {
        public static FileFactComparer Instance { get; } = new();
        public int Compare(ModFileFact? left, ModFileFact? right) => ModKeyComparer.Instance.Compare(left!.Mod, right!.Mod) is var result and not 0 ? result : StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath);
    }
    private sealed class OverrideFactComparer : IComparer<ModOverrideFact>
    {
        public static OverrideFactComparer Instance { get; } = new();
        public int Compare(ModOverrideFact? left, ModOverrideFact? right)
        {
            var mod = ModKeyComparer.Instance.Compare(left!.Mod, right!.Mod); if (mod != 0) return mod;
            var path = StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath); return path != 0 ? path : left.LineNumber.CompareTo(right.LineNumber);
        }
    }
}

internal static class SetExtensions
{
    public static IReadOnlySet<T> SymmetricExcept<T>(this IReadOnlySet<T> left, IReadOnlySet<T> right) => left.Where(item => !right.Contains(item)).Concat(right.Where(item => !left.Contains(item))).ToHashSet();
}
