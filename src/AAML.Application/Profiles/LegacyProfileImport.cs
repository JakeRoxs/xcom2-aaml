using System.Security.Cryptography;
using System.Text;
using AAML.Application.Common;
using AAML.Application.Settings;
using AAML.Domain.Mods;
using AAML.Domain.Profiles;

namespace AAML.Application.Profiles;

public sealed record LegacyProfileEntry(ModSource Source, string PackageId, ulong? WorkshopId, int LineNumber, string? DisplayName = null, string? Category = null, IReadOnlyList<string>? Tags = null, string? RawText = null);
public sealed record LegacyProfileParseResult(IReadOnlyList<LegacyProfileEntry> Entries, IReadOnlyList<string> Diagnostics);
public sealed record LegacyProfileImportResult(ModProfile Profile, bool Imported, IReadOnlyList<string> Diagnostics);
public enum LegacyTaxonomyDisposition { Ignore, ProfileMetadata, AdoptIntoApplication }
public sealed record LegacyProfilePreview(string SourceFingerprint, IReadOnlyList<LegacyProfileEntry> Entries, IReadOnlyList<string> Diagnostics, string Report);

public interface ILegacyProfileParser
{
    Result<LegacyProfileParseResult> Parse(string contents);
}

public interface ILegacyProfileImportService
{
    Task<Result<LegacyProfileImportResult>> ImportAsync(string name, string contents, ApplicationSettings settings, CancellationToken cancellationToken);
    Result<LegacyProfilePreview> Preview(string contents);
    Task<Result<LegacyProfileImportResult>> ImportAsync(string name, LegacyProfilePreview preview, LegacyTaxonomyDisposition taxonomy, ApplicationSettings settings, IReadOnlyList<ModInstallation> installations, CancellationToken cancellationToken);
}

/// <summary>Converts legacy text exports into portable profiles without replacing modern data.</summary>
public sealed class LegacyProfileImportService(ILegacyProfileParser parser, IProfileRepository profiles) : ILegacyProfileImportService
{
    public async Task<Result<LegacyProfileImportResult>> ImportAsync(string name, string contents, ApplicationSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result<LegacyProfileImportResult>.Failure(new Error("profile.legacy_name_required", "Enter a name for the legacy profile.", ErrorKind.Validation));

        var preview = Preview(contents);
        if (!preview.IsSuccess) return Result<LegacyProfileImportResult>.Failure(preview.Error!);
        return await ImportAsync(name, preview.Value!, LegacyTaxonomyDisposition.Ignore, settings, [], cancellationToken).ConfigureAwait(false);
    }

    public Result<LegacyProfilePreview> Preview(string contents)
    {
        var parsed = parser.Parse(contents);
        if (!parsed.IsSuccess) return Result<LegacyProfilePreview>.Failure(parsed.Error!);
        var canonical = string.Join('\n', parsed.Value!.Entries.Select(entry => $"{entry.Source}|{entry.PackageId.ToUpperInvariant()}|{entry.WorkshopId}|{entry.Category?.ToUpperInvariant()}|{string.Join(';', entry.Tags ?? []).ToUpperInvariant()}"));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var diagnostics = parsed.Value.Diagnostics.Concat(DuplicateDiagnostics(parsed.Value.Entries)).ToArray();
        var report = $"Legacy AML list preview\nSource preserved: yes\nFingerprint: {fingerprint}\nEntries: {parsed.Value.Entries.Count}\n" + string.Join('\n', parsed.Value.Entries.Select(entry => $"Line {entry.LineNumber}: {entry.PackageId} | {entry.Source} | {entry.WorkshopId} | {entry.Category} | {string.Join(';', entry.Tags ?? [])}")) + (diagnostics.Length > 0 ? "\nDiagnostics:\n" + string.Join('\n', diagnostics) : string.Empty);
        return Result<LegacyProfilePreview>.Success(new LegacyProfilePreview(fingerprint, parsed.Value.Entries, diagnostics, report));
    }

    public async Task<Result<LegacyProfileImportResult>> ImportAsync(string name, LegacyProfilePreview preview, LegacyTaxonomyDisposition taxonomy, ApplicationSettings settings, IReadOnlyList<ModInstallation> installations, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name)) return Result<LegacyProfileImportResult>.Failure(new Error("profile.legacy_name_required", "Enter a name for the legacy profile.", ErrorKind.Validation));
        if (preview.Entries.Count == 0)
            return Result<LegacyProfileImportResult>.Failure(new Error("profile.legacy_empty", "The legacy profile contains no importable Workshop mods.", ErrorKind.InvalidData));

        var canonical = string.Join('\n', new[]
        {
            settings.SelectedGame.ToString(),
            preview.SourceFingerprint,
            taxonomy.ToString()
        });
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        var id = new ProfileId(new Guid(hash.AsSpan(0, 16)));
        var profileName = name.Trim();
        var profile = new ModProfile(
            id,
            profileName,
            settings.SelectedGame,
            preview.Entries.Select((entry, order) => new ProfileModEntry(entry.Source, new PackageId(entry.PackageId), entry.WorkshopId is { } workshopId ? new WorkshopId(workshopId) : null, order)).ToArray(),
            settings.LaunchArguments.ToArray(),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            taxonomy == LegacyTaxonomyDisposition.ProfileMetadata ? new LegacyProfileMetadata(preview.SourceFingerprint, preview.Entries.Select((entry, order) => new LegacyProfileRowMetadata(order, entry.DisplayName, entry.Category, entry.Tags ?? [], entry.LineNumber)).ToArray()) : null);

        var existing = await profiles.ListAsync(cancellationToken).ConfigureAwait(false);
        if (!existing.IsSuccess) return Result<LegacyProfileImportResult>.Failure(existing.Error!);
        var sameId = existing.Value!.SingleOrDefault(candidate => candidate.Id == id);
        if (sameId is not null)
        {
            if (!Equivalent(sameId, profile))
                return Result<LegacyProfileImportResult>.Failure(new Error("profile.legacy_id_conflict", "A different profile already uses the deterministic legacy migration ID.", ErrorKind.Conflict));
            return Result<LegacyProfileImportResult>.Success(new LegacyProfileImportResult(sameId, false, preview.Diagnostics));
        }

        var added = await profiles.AddAsync(profile, cancellationToken).ConfigureAwait(false);
        return added.IsSuccess
            ? Result<LegacyProfileImportResult>.Success(new LegacyProfileImportResult(profile, true, preview.Diagnostics))
            : Result<LegacyProfileImportResult>.Failure(added.Error!);
    }

    private static IEnumerable<string> DuplicateDiagnostics(IReadOnlyList<LegacyProfileEntry> entries) => entries.GroupBy(entry => entry.PackageId, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).Select(group => $"Duplicate package '{group.Key}' appears on lines {string.Join(", ", group.Select(entry => entry.LineNumber))}.");

    private static bool Equivalent(ModProfile left, ModProfile right) =>
        left.GameVariant == right.GameVariant &&
        left.LaunchArguments.SequenceEqual(right.LaunchArguments) &&
        left.Mods.SequenceEqual(right.Mods) && Equivalent(left.LegacyMetadata, right.LegacyMetadata);

    private static bool Equivalent(LegacyProfileMetadata? left, LegacyProfileMetadata? right) =>
        left is null && right is null || left is not null && right is not null &&
        left.SourceFingerprint == right.SourceFingerprint && left.Rows.Count == right.Rows.Count &&
        left.Rows.Zip(right.Rows).All(pair => pair.First.Order == pair.Second.Order && pair.First.DisplayName == pair.Second.DisplayName && pair.First.Category == pair.Second.Category && pair.First.SourceLine == pair.Second.SourceLine && pair.First.Tags.SequenceEqual(pair.Second.Tags));
}
