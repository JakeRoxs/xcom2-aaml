using AAML.Application.Common;
using AAML.Application.Profiles;
using AAML.Domain.Games;
using AAML.Domain.Launching;
using AAML.Domain.Mods;
using AAML.Domain.Profiles;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace AAML.Infrastructure.Common.Profiles;

/// <summary>Imports and exports one portable profile without machine-specific paths.</summary>
public sealed class JsonProfileInterchange(IProfileRepository repository) : IProfileInterchange
{
    public async Task<Result<string>> ExportAsync(ProfileId id, CancellationToken cancellationToken)
    {
        var loaded = await repository.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess) return Result<string>.Failure(loaded.Error!);
        var profile = loaded.Value!;
        var document = new InterchangeDocument(1, profile.Id.Value, profile.Name, profile.GameVariant,
            profile.Mods.OrderBy(mod => mod.Order).Select(mod => new ModDocument(mod.Source, mod.PackageId.Value, mod.WorkshopId?.Value, mod.Order)).ToArray(),
            profile.LaunchArguments.Select(argument => argument.Value).ToArray(), profile.CreatedAt, profile.UpdatedAt,
            profile.LegacyMetadata is null ? null : new LegacyMetadataDocument(profile.LegacyMetadata.SourceFingerprint, profile.LegacyMetadata.Rows.Select(row => new LegacyRowDocument(row.Order, row.DisplayName, row.Category, row.Tags, row.SourceLine)).ToArray()));
        return Result<string>.Success(JsonConvert.SerializeObject(document, Settings()) + Environment.NewLine);
    }

    public async Task<Result<ModProfile>> ImportAsync(string document, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(document)) return Result<ModProfile>.Failure(new Error("profile.import_empty", "The profile document is empty.", ErrorKind.InvalidData));
        try
        {
            var parsed = JsonConvert.DeserializeObject<InterchangeDocument>(document, Settings()) ?? throw new InvalidDataException("The profile document is empty.");
            if (parsed.SchemaVersion != 1 || parsed.Id == Guid.Empty || string.IsNullOrWhiteSpace(parsed.Name)) throw new InvalidDataException("The profile schema, ID, or name is invalid.");
            if (parsed.Mods is null || parsed.LaunchArguments is null) throw new InvalidDataException("The profile mod and argument arrays are required.");
            if (parsed.Mods.Any(mod => mod.Order < 0) || parsed.Mods.Select(mod => mod.Order).Distinct().Count() != parsed.Mods.Count) throw new InvalidDataException("Profile mod order must be unique and nonnegative.");
            var listed = await repository.ListAsync(cancellationToken).ConfigureAwait(false);
            if (!listed.IsSuccess) return Result<ModProfile>.Failure(listed.Error!);
            var id = new ProfileId(parsed.Id);
            if (listed.Value!.Any(profile => profile.Id == id)) return Result<ModProfile>.Failure(new Error("profile.id_conflict", $"Profile ID already exists: {id}", ErrorKind.Conflict));
            var profile = new ModProfile(id, parsed.Name.Trim(), parsed.GameVariant,
                parsed.Mods.OrderBy(mod => mod.Order).Select(mod => new ProfileModEntry(mod.Source, new PackageId(mod.PackageId), mod.WorkshopId.HasValue ? new WorkshopId(mod.WorkshopId.Value) : null, mod.Order)).ToArray(),
                parsed.LaunchArguments.Select(argument => new LaunchArgument(argument)).ToArray(), parsed.CreatedAt, parsed.UpdatedAt,
                parsed.LegacyMetadata is null ? null : new LegacyProfileMetadata(parsed.LegacyMetadata.SourceFingerprint, parsed.LegacyMetadata.Rows.Select(row => new LegacyProfileRowMetadata(row.Order, row.DisplayName, row.Category, row.Tags, row.SourceLine)).ToArray()));
            var saved = await repository.SaveAsync(profile, cancellationToken).ConfigureAwait(false);
            return saved.IsSuccess ? Result<ModProfile>.Success(profile) : Result<ModProfile>.Failure(saved.Error!);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
        {
            return Result<ModProfile>.Failure(new Error("profile.import_invalid", exception.Message, ErrorKind.InvalidData));
        }
    }

    private static JsonSerializerSettings Settings() => new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Include,
        MissingMemberHandling = MissingMemberHandling.Error,
        Converters = { new StringEnumConverter() }
    };

    private sealed record InterchangeDocument(int SchemaVersion, Guid Id, string Name, GameVariant GameVariant, IReadOnlyList<ModDocument> Mods, IReadOnlyList<string> LaunchArguments, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, LegacyMetadataDocument? LegacyMetadata = null);
    private sealed record ModDocument(ModSource Source, string PackageId, ulong? WorkshopId, int Order);
    private sealed record LegacyMetadataDocument(string SourceFingerprint, IReadOnlyList<LegacyRowDocument> Rows);
    private sealed record LegacyRowDocument(int Order, string? DisplayName, string? Category, IReadOnlyList<string> Tags, int SourceLine);
}
