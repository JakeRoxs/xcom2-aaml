using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Application.Profiles;
using AAML.Domain.Games;
using AAML.Domain.Launching;
using AAML.Domain.Mods;
using AAML.Domain.Profiles;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace AAML.Infrastructure.Common.Profiles;

/// <summary>Stores portable named profiles in one versioned, atomic per-user document.</summary>
public sealed class JsonProfileRepository(IApplicationPaths paths, IAtomicTextWriter writer) : IProfileRepository
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string ProfilePath => Path.Combine(paths.DataDirectory, "Profiles", "profiles.json");

    public async Task<Result<IReadOnlyList<ModProfile>>> ListAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ReadAsync(cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task<Result<ModProfile>> GetAsync(ProfileId id, CancellationToken cancellationToken)
    {
        var listed = await ListAsync(cancellationToken).ConfigureAwait(false);
        if (!listed.IsSuccess) return Result<ModProfile>.Failure(listed.Error!);
        var profile = listed.Value!.SingleOrDefault(profile => profile.Id == id);
        return profile is null
            ? Result<ModProfile>.Failure(new Error("profile.not_found", $"Profile does not exist: {id}", ErrorKind.NotFound))
            : Result<ModProfile>.Success(profile);
    }

    public async Task<Result> SaveAsync(ModProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var listed = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!listed.IsSuccess) return Result.Failure(listed.Error!);
            var existingProfiles = listed.Value!;
            if (existingProfiles.Any(existing => existing.Id != profile.Id && existing.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase)))
                return Result.Failure(new Error("profile.name_conflict", $"A profile already uses the name '{profile.Name}'.", ErrorKind.Conflict));
            var profiles = existingProfiles.Where(existing => existing.Id != profile.Id).Append(profile).OrderBy(existing => existing.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            return await WriteAsync(profiles, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task<Result> AddAsync(ModProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var listed = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!listed.IsSuccess) return Result.Failure(listed.Error!);
            var existingProfiles = listed.Value!;
            if (existingProfiles.Any(existing => existing.Id == profile.Id))
                return Result.Failure(new Error("profile.id_conflict", $"A profile already uses ID '{profile.Id}'.", ErrorKind.Conflict));
            if (existingProfiles.Any(existing => existing.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase)))
                return Result.Failure(new Error("profile.name_conflict", $"A profile already uses the name '{profile.Name}'.", ErrorKind.Conflict));
            return await WriteAsync(existingProfiles.Append(profile).OrderBy(existing => existing.Name, StringComparer.OrdinalIgnoreCase).ToArray(), cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task<Result> DeleteAsync(ProfileId id, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var listed = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!listed.IsSuccess) return Result.Failure(listed.Error!);
            var existingProfiles = listed.Value!;
            if (existingProfiles.All(profile => profile.Id != id)) return Result.Failure(new Error("profile.not_found", $"Profile does not exist: {id}", ErrorKind.NotFound));
            return await WriteAsync(existingProfiles.Where(profile => profile.Id != id).ToArray(), cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<Result<IReadOnlyList<ModProfile>>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(ProfilePath)) return Result<IReadOnlyList<ModProfile>>.Success([]);
        try
        {
            var json = await File.ReadAllTextAsync(ProfilePath, cancellationToken).ConfigureAwait(false);
            var document = JsonConvert.DeserializeObject<ProfileCollectionDocument>(json, SerializerSettings()) ?? throw new InvalidDataException("Profile document is empty.");
            if (document.SchemaVersion != 1) throw new InvalidDataException($"Unsupported profile schema: {document.SchemaVersion}.");
            var profiles = (document.Profiles ?? []).Select(ToDomain).ToArray();
            if (profiles.Select(profile => profile.Id).Distinct().Count() != profiles.Length) throw new InvalidDataException("Profile IDs must be unique.");
            return Result<IReadOnlyList<ModProfile>>.Success(profiles);
        }
        catch (OperationCanceledException) { return Result<IReadOnlyList<ModProfile>>.Failure(new Error("profile.read_cancelled", "Profile loading was cancelled.", ErrorKind.Cancelled)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            return Result<IReadOnlyList<ModProfile>>.Failure(new Error("profile.document_invalid", exception.Message, ErrorKind.InvalidData));
        }
    }

    private Task<Result> WriteAsync(IReadOnlyList<ModProfile> profiles, CancellationToken cancellationToken)
    {
        var document = new ProfileCollectionDocument(1, profiles.Select(ToDocument).ToArray());
        var json = JsonConvert.SerializeObject(document, SerializerSettings()) + Environment.NewLine;
        return writer.WriteAsync(ProfilePath, json, cancellationToken);
    }

    private static ModProfile ToDomain(ProfileDocument document)
    {
        if (document.Id == Guid.Empty || string.IsNullOrWhiteSpace(document.Name)) throw new InvalidDataException("Profile identity and name are required.");
        if (document.Mods.Any(mod => mod.Order < 0) || document.Mods.Select(mod => mod.Order).Distinct().Count() != document.Mods.Count) throw new InvalidDataException("Profile mod order must be unique and nonnegative.");
        return new ModProfile(new ProfileId(document.Id), document.Name, document.GameVariant,
            document.Mods.OrderBy(mod => mod.Order).Select(mod => new ProfileModEntry(mod.Source, new PackageId(mod.PackageId), mod.WorkshopId.HasValue ? new WorkshopId(mod.WorkshopId.Value) : null, mod.Order)).ToArray(),
            document.LaunchArguments.Select(argument => new LaunchArgument(argument)).ToArray(), document.CreatedAt, document.UpdatedAt,
            document.LegacyMetadata is null ? null : new LegacyProfileMetadata(document.LegacyMetadata.SourceFingerprint, document.LegacyMetadata.Rows.Select(row => new LegacyProfileRowMetadata(row.Order, row.DisplayName, row.Category, row.Tags, row.SourceLine)).ToArray()));
    }

    private static ProfileDocument ToDocument(ModProfile profile) => new(profile.Id.Value, profile.Name, profile.GameVariant,
        profile.Mods.OrderBy(mod => mod.Order).Select(mod => new ProfileModDocument(mod.Source, mod.PackageId.Value, mod.WorkshopId?.Value, mod.Order)).ToArray(),
        profile.LaunchArguments.Select(argument => argument.Value).ToArray(), profile.CreatedAt, profile.UpdatedAt,
        profile.LegacyMetadata is null ? null : new LegacyMetadataDocument(profile.LegacyMetadata.SourceFingerprint, profile.LegacyMetadata.Rows.Select(row => new LegacyRowDocument(row.Order, row.DisplayName, row.Category, row.Tags, row.SourceLine)).ToArray()));

    private static JsonSerializerSettings SerializerSettings() => new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Include,
        Converters = { new StringEnumConverter() }
    };

    private sealed record ProfileCollectionDocument(int SchemaVersion, IReadOnlyList<ProfileDocument>? Profiles);
    private sealed record ProfileDocument(Guid Id, string Name, GameVariant GameVariant, IReadOnlyList<ProfileModDocument> Mods, IReadOnlyList<string> LaunchArguments, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, LegacyMetadataDocument? LegacyMetadata = null);
    private sealed record ProfileModDocument(ModSource Source, string PackageId, ulong? WorkshopId, int Order);
    private sealed record LegacyMetadataDocument(string SourceFingerprint, IReadOnlyList<LegacyRowDocument> Rows);
    private sealed record LegacyRowDocument(int Order, string? DisplayName, string? Category, IReadOnlyList<string> Tags, int SourceLine);
}
