using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Application.Settings;
using AAML.Domain.Mods;
using AAML.Domain.Profiles;
using AAML.Application.Mods.Dependencies;

namespace AAML.Application.Profiles;

public interface IProfileRepository
{
    Task<Result<IReadOnlyList<ModProfile>>> ListAsync(CancellationToken cancellationToken);
    Task<Result<ModProfile>> GetAsync(ProfileId id, CancellationToken cancellationToken);
    Task<Result> AddAsync(ModProfile profile, CancellationToken cancellationToken);
    Task<Result> SaveAsync(ModProfile profile, CancellationToken cancellationToken);
    Task<Result> DeleteAsync(ProfileId id, CancellationToken cancellationToken);
}

public sealed record ProfileApplyDiagnostic(string Code, string Message, ProfileModEntry Entry);
public sealed record ProfileApplyResult(ModProfile Profile, ApplicationSettings Settings, bool Applied, IReadOnlyList<ProfileApplyDiagnostic> Diagnostics);

public interface IProfileService
{
    Task<Result<IReadOnlyList<ModProfile>>> ListAsync(CancellationToken cancellationToken);
    Task<Result<ModProfile>> CreateAsync(string name, ApplicationSettings settings, IReadOnlyList<ModInstallation> installations, CancellationToken cancellationToken);
    Task<Result<ModProfile>> RenameAsync(ProfileId id, string name, CancellationToken cancellationToken);
    Task<Result<ModProfile>> DuplicateAsync(ProfileId id, string name, CancellationToken cancellationToken);
    Task<Result<ProfileApplyResult>> ApplyAsync(ProfileId id, ApplicationSettings settings, IReadOnlyList<ModInstallation> installations, CancellationToken cancellationToken);
    Task<Result> DeleteAsync(ProfileId id, CancellationToken cancellationToken);
}

/// <summary>Creates and applies portable profiles while preserving non-profile mod metadata.</summary>
public sealed class ProfileService(IProfileRepository profiles, ISettingsRepository settingsRepository, IModDependencyService dependencies) : IProfileService
{
    public Task<Result<IReadOnlyList<ModProfile>>> ListAsync(CancellationToken cancellationToken) => profiles.ListAsync(cancellationToken);
    public Task<Result> DeleteAsync(ProfileId id, CancellationToken cancellationToken) => profiles.DeleteAsync(id, cancellationToken);

    public async Task<Result<ModProfile>> RenameAsync(ProfileId id, string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name)) return Result<ModProfile>.Failure(new Error("profile.name_required", "A profile name is required.", ErrorKind.Validation));
        var loaded = await profiles.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess) return Result<ModProfile>.Failure(loaded.Error!);
        var updated = loaded.Value! with { Name = name.Trim(), UpdatedAt = DateTimeOffset.UtcNow };
        var saved = await profiles.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? Result<ModProfile>.Success(updated) : Result<ModProfile>.Failure(saved.Error!);
    }

    public async Task<Result<ModProfile>> DuplicateAsync(ProfileId id, string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name)) return Result<ModProfile>.Failure(new Error("profile.name_required", "A profile name is required.", ErrorKind.Validation));
        var loaded = await profiles.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess) return Result<ModProfile>.Failure(loaded.Error!);
        var now = DateTimeOffset.UtcNow;
        var copy = loaded.Value! with { Id = new ProfileId(Guid.NewGuid()), Name = name.Trim(), CreatedAt = now, UpdatedAt = now };
        var saved = await profiles.SaveAsync(copy, cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? Result<ModProfile>.Success(copy) : Result<ModProfile>.Failure(saved.Error!);
    }

    public async Task<Result<ModProfile>> CreateAsync(string name, ApplicationSettings settings, IReadOnlyList<ModInstallation> installations, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name)) return Result<ModProfile>.Failure(new Error("profile.name_required", "A profile name is required.", ErrorKind.Validation));
        var installed = installations.ToDictionary(mod => mod.Key);
        var active = settings.ModIntents.Where(intent => intent.IsActive).OrderBy(intent => intent.ExplicitOrder ?? int.MaxValue).ThenBy(intent => intent.Mod.LocationIdentity, StringComparer.Ordinal).ToArray();
        var missing = active.FirstOrDefault(intent => !installed.ContainsKey(intent.Mod));
        if (missing is not null) return Result<ModProfile>.Failure(new Error("profile.active_mod_missing", $"Active mod is not installed: {missing.Mod}", ErrorKind.NotFound));
        var now = DateTimeOffset.UtcNow;
        var entries = active.Select((intent, order) =>
        {
            var mod = installed[intent.Mod];
            return new ProfileModEntry(mod.Key.Source, mod.PackageId, mod.WorkshopId, order);
        }).ToArray();
        var profile = new ModProfile(new ProfileId(Guid.NewGuid()), name.Trim(), settings.SelectedGame, entries, settings.LaunchArguments.ToArray(), now, now);
        var saved = await profiles.SaveAsync(profile, cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? Result<ModProfile>.Success(profile) : Result<ModProfile>.Failure(saved.Error!);
    }

    public async Task<Result<ProfileApplyResult>> ApplyAsync(ProfileId id, ApplicationSettings settings, IReadOnlyList<ModInstallation> installations, CancellationToken cancellationToken)
    {
        var loaded = await profiles.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess) return Result<ProfileApplyResult>.Failure(loaded.Error!);
        var resolved = new Dictionary<ProfileModEntry, ModInstallation>();
        var diagnostics = new List<ProfileApplyDiagnostic>();
        foreach (var entry in loaded.Value!.Mods.OrderBy(entry => entry.Order))
        {
            var matches = installations.Where(mod => entry.WorkshopId is { } workshop
                ? mod.WorkshopId == workshop
                : mod.Key.Source == entry.Source && mod.PackageId == entry.PackageId).ToArray();
            if (matches.Length == 0) diagnostics.Add(new ProfileApplyDiagnostic("profile.mod_missing", $"Profile mod is not installed: {entry.PackageId.Value}", entry));
            else if (matches.Length > 1) diagnostics.Add(new ProfileApplyDiagnostic("profile.mod_ambiguous", $"More than one installation matches: {entry.PackageId.Value}", entry));
            else resolved[entry] = matches[0];
        }
        if (diagnostics.Count > 0) return Result<ProfileApplyResult>.Success(new ProfileApplyResult(loaded.Value, settings, false, diagnostics));

        var workshopEntries = resolved.Where(pair => pair.Key.WorkshopId.HasValue).ToArray();
        var roots = workshopEntries.Select(pair => pair.Key.WorkshopId!.Value).ToHashSet();
        var installedWorkshop = installations.Where(mod => mod.WorkshopId.HasValue).Select(mod => mod.WorkshopId!.Value).ToHashSet();
        var ignored = new Dictionary<WorkshopId, IReadOnlySet<WorkshopId>>();
        var currentIntents = settings.ModIntents.ToDictionary(intent => intent.Mod);
        foreach (var pair in workshopEntries)
            if (currentIntents.TryGetValue(pair.Value.Key, out var intent)) ignored[pair.Key.WorkshopId!.Value] = intent.IgnoredDependencies;
        var dependencyResult = await dependencies.EvaluateAsync(roots, installedWorkshop, roots, ignored, cancellationToken).ConfigureAwait(false);
        if (!dependencyResult.IsSuccess) return Result<ProfileApplyResult>.Failure(dependencyResult.Error!);
        var dependencyDiagnostics = dependencyResult.Value!.Issues.Select(issue =>
        {
            var entry = loaded.Value.Mods.FirstOrDefault(mod => mod.WorkshopId == issue.Parent) ?? loaded.Value.Mods.First();
            return new ProfileApplyDiagnostic($"profile.dependency_{issue.Kind.ToString().ToLowerInvariant()}", issue.Message, entry);
        }).ToArray();
        if (dependencyResult.Value.HasBlockingIssues)
            return Result<ProfileApplyResult>.Success(new ProfileApplyResult(loaded.Value, settings, false, dependencyDiagnostics));

        var targetOrder = resolved.ToDictionary(pair => pair.Value.Key, pair => pair.Key.Order);
        var intents = settings.ModIntents.ToDictionary(intent => intent.Mod);
        foreach (var installation in installations)
        {
            var active = targetOrder.TryGetValue(installation.Key, out var order);
            if (intents.TryGetValue(installation.Key, out var existing))
            {
                var updated = existing with { IsActive = active, ExplicitOrder = active ? order : null };
                if (IsEmpty(updated)) intents.Remove(installation.Key); else intents[installation.Key] = updated;
            }
            else if (active) intents[installation.Key] = new ModUserIntent(installation.Key, true, false, order, null, null, new HashSet<TagId>(), null, new HashSet<WorkshopId>());
        }
        var updatedSettings = settings with
        {
            SelectedGame = loaded.Value.GameVariant,
            LaunchArguments = loaded.Value.LaunchArguments.ToArray(),
            ModIntents = intents.Values.OrderBy(intent => intent.ExplicitOrder ?? int.MaxValue).ThenBy(intent => intent.Mod.LocationIdentity, StringComparer.Ordinal).ToArray()
        };
        var saved = await settingsRepository.SaveAsync(updatedSettings, cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess
            ? Result<ProfileApplyResult>.Success(new ProfileApplyResult(loaded.Value, updatedSettings, true, dependencyDiagnostics))
            : Result<ProfileApplyResult>.Failure(saved.Error!);
    }

    private static bool IsEmpty(ModUserIntent intent) => !intent.IsActive && !intent.IsHidden && !intent.ExplicitOrder.HasValue && string.IsNullOrWhiteSpace(intent.ManualName) && intent.Category is null && intent.Tags.Count == 0 && string.IsNullOrWhiteSpace(intent.Note) && intent.IgnoredDependencies.Count == 0;
}
