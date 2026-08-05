using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Application.Settings;
using AAML.Domain.Mods;

namespace AAML.Application.Mods.Duplicates;

public sealed record ModDuplicateGroup(PackageId PackageId, IReadOnlyList<ModInstallation> Installations, ModKey? Preferred, bool IsResolved, string? Issue);
public sealed record ModDuplicateReport(IReadOnlyList<ModDuplicateGroup> Groups, IReadOnlyDictionary<ModKey, DuplicateStatus> Statuses)
{
    public DuplicateStatus Status(ModKey key) => Statuses.GetValueOrDefault(key, DuplicateStatus.None);
}

public interface IModDuplicateAnalyzer
{
    ModDuplicateReport Analyze(IReadOnlyList<ModInstallation> installations, IReadOnlyList<DuplicatePreference> preferences);
}

public sealed class ModDuplicateAnalyzer : IModDuplicateAnalyzer
{
    public ModDuplicateReport Analyze(IReadOnlyList<ModInstallation> installations, IReadOnlyList<DuplicatePreference> preferences)
    {
        var statuses = installations.ToDictionary(mod => mod.Key, _ => DuplicateStatus.None);
        var groups = new List<ModDuplicateGroup>();
        foreach (var group in installations.GroupBy(mod => mod.PackageId.Value, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var members = group.OrderBy(mod => mod.Key.Source).ThenBy(mod => mod.Key.LocationIdentity, StringComparer.Ordinal).ToArray();
            var matching = preferences.Where(preference => preference.PackageId.Value.Equals(group.Key, StringComparison.OrdinalIgnoreCase)).ToArray();
            var preferred = matching.Length == 1 ? members.SingleOrDefault(mod => mod.Key == matching[0].PreferredInstallation) : null;
            var valid = preferred is not null && preferred.DescriptorState == DescriptorState.Enabled;
            var issue = matching.Length > 1 ? "Multiple preferences are persisted." : matching.Length == 1 && preferred is null ? "The preferred installation is missing or changed package identity." : preferred?.DescriptorState == DescriptorState.Disabled ? "The preferred descriptor is disabled." : null;
            foreach (var member in members) statuses[member.Key] = valid ? member.Key == preferred!.Key ? DuplicateStatus.Preferred : DuplicateStatus.Secondary : DuplicateStatus.Unresolved;
            groups.Add(new ModDuplicateGroup(members[0].PackageId, members, valid ? preferred!.Key : null, valid, issue));
        }
        return new ModDuplicateReport(groups, statuses);
    }
}

public interface IDuplicatePreferenceService
{
    Task<Result<ApplicationSettings>> PreferAsync(ApplicationSettings settings, IReadOnlyList<ModInstallation> installations, ModKey preferred, CancellationToken cancellationToken);
    Task<Result<ApplicationSettings>> ClearAsync(ApplicationSettings settings, IReadOnlyList<ModInstallation> installations, PackageId packageId, CancellationToken cancellationToken);
}

public sealed class DuplicatePreferenceService(ISettingsRepository repository) : IDuplicatePreferenceService
{
    public Task<Result<ApplicationSettings>> PreferAsync(ApplicationSettings settings, IReadOnlyList<ModInstallation> installations, ModKey preferred, CancellationToken cancellationToken)
    {
        var selected = installations.SingleOrDefault(mod => mod.Key == preferred);
        if (selected is null) return Task.FromResult(Result<ApplicationSettings>.Failure(new Error("duplicates.mod_missing", "The selected installation is no longer discovered.", ErrorKind.NotFound)));
        var group = installations.Where(mod => mod.PackageId.Value.Equals(selected.PackageId.Value, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (group.Length < 2) return Task.FromResult(Result<ApplicationSettings>.Failure(new Error("duplicates.not_duplicate", "The selected installation is not part of a duplicate package group.", ErrorKind.Validation)));
        if (selected.DescriptorState != DescriptorState.Enabled) return Task.FromResult(Result<ApplicationSettings>.Failure(new Error("duplicates.descriptor_disabled", "A disabled descriptor cannot be preferred.", ErrorKind.Validation)));
        var active = settings.ModIntents.Where(intent => group.Any(mod => mod.Key == intent.Mod) && intent.IsActive).ToArray();
        var transferOrder = settings.ModIntents.SingleOrDefault(intent => intent.Mod == preferred)?.ExplicitOrder ?? active.Select(intent => intent.ExplicitOrder).Where(order => order.HasValue).DefaultIfEmpty().Min();
        var intents = settings.ModIntents.ToDictionary(intent => intent.Mod);
        foreach (var mod in group)
        {
            var intent = intents.GetValueOrDefault(mod.Key) ?? new ModUserIntent(mod.Key, false, false, null, null, null, new HashSet<TagId>(), null, new HashSet<WorkshopId>());
            intents[mod.Key] = intent with { IsActive = active.Length > 0 && mod.Key == preferred, ExplicitOrder = mod.Key == preferred && active.Length > 0 ? transferOrder : intent.ExplicitOrder };
        }
        var preferences = (settings.DuplicatePreferences ?? []).Where(item => !item.PackageId.Value.Equals(selected.PackageId.Value, StringComparison.OrdinalIgnoreCase)).Append(new DuplicatePreference(selected.PackageId, preferred)).ToArray();
        return SaveAsync(settings with { DuplicatePreferences = preferences, ModIntents = intents.Values.OrderBy(intent => intent.Mod.LocationIdentity, StringComparer.Ordinal).ToArray() }, cancellationToken);
    }

    public Task<Result<ApplicationSettings>> ClearAsync(ApplicationSettings settings, IReadOnlyList<ModInstallation> installations, PackageId packageId, CancellationToken cancellationToken)
    {
        var keys = installations.Where(mod => mod.PackageId.Value.Equals(packageId.Value, StringComparison.OrdinalIgnoreCase)).Select(mod => mod.Key).ToHashSet();
        var intents = settings.ModIntents.Select(intent => keys.Contains(intent.Mod) ? intent with { IsActive = false } : intent).ToArray();
        var preferences = (settings.DuplicatePreferences ?? []).Where(item => !item.PackageId.Value.Equals(packageId.Value, StringComparison.OrdinalIgnoreCase)).ToArray();
        return SaveAsync(settings with { DuplicatePreferences = preferences, ModIntents = intents }, cancellationToken);
    }

    private async Task<Result<ApplicationSettings>> SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken)
    {
        var result = await repository.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Result<ApplicationSettings>.Success(settings) : Result<ApplicationSettings>.Failure(result.Error!);
    }
}

public static class ModDuplicateActivationPolicy
{
    public static Result Validate(IReadOnlyList<ModInstallation> installations, IEnumerable<ModIntentEdit> edits, ModDuplicateReport report)
    {
        var active = edits.Where(edit => edit.IsActive).Select(edit => edit.Mod).ToHashSet();
        foreach (var group in report.Groups)
        {
            var activeMembers = group.Installations.Where(mod => active.Contains(mod.Key)).ToArray();
            if (activeMembers.Length == 0) continue;
            if (!group.IsResolved) return Result.Failure(new Error("duplicates.unresolved_active", $"Resolve duplicate package '{group.PackageId}' before activating it.", ErrorKind.Conflict));
            if (activeMembers.Length != 1 || activeMembers[0].Key != group.Preferred) return Result.Failure(new Error("duplicates.invalid_active", $"Only the preferred installation of duplicate package '{group.PackageId}' may be active.", ErrorKind.Conflict));
        }
        return Result.Success();
    }
}
