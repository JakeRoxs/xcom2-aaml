namespace AAML.Infrastructure.Common.Compatibility.Mods;

/// <summary>State flags relevant to legacy dependency and duplicate calculations.</summary>
[Flags]
public enum LegacyModState
{
    None = 0,
    NotLoaded = 32,
    NotInstalled = 64,
    DuplicatePrimary = 128,
    DuplicateDisabled = 256
}

/// <summary>A minimal mod projection used by compatibility calculations.</summary>
public sealed record LegacyModFact(
    string Key,
    string ModId,
    long WorkshopId,
    bool IsActive,
    LegacyModState State,
    IReadOnlyList<long> Dependencies,
    IReadOnlySet<long> IgnoredDependencies,
    DateTimeOffset? DateAdded = null,
    bool DescriptorDisabled = false);

/// <summary>A resolved dependency and whether duplicate substitution occurred.</summary>
public sealed record LegacyDependencyResolution(long RequestedWorkshopId, LegacyModFact? Mod, bool Substituted);

/// <summary>Role assigned to a duplicate candidate by the legacy workaround.</summary>
public enum LegacyDuplicateRole
{
    Unresolved,
    Primary,
    Disabled
}

/// <summary>A duplicate role and optional descriptor-disable operation.</summary>
public sealed record LegacyDuplicateDecision(string Key, LegacyDuplicateRole Role, bool DisableDescriptor);

/// <summary>Pure projections of legacy dependency and duplicate behavior.</summary>
public static class LegacyModAnalysis
{
    /// <summary>Resolves installed dependencies, including exact-case duplicate-primary substitution.</summary>
    public static IReadOnlyList<LegacyDependencyResolution> ResolveDependencies(LegacyModFact mod, IReadOnlyList<LegacyModFact> installed)
    {
        var dependencies = mod.Dependencies.Except(mod.IgnoredDependencies).ToArray();
        return dependencies.Select(id =>
        {
            var resolved = installed.FirstOrDefault(candidate => candidate.WorkshopId == id);
            var substituted = false;
            if (resolved is not null && resolved.State.HasFlag(LegacyModState.DuplicateDisabled))
            {
                var primary = installed.FirstOrDefault(candidate => candidate.ModId == resolved.ModId && candidate.State.HasFlag(LegacyModState.DuplicatePrimary));
                if (primary is not null)
                {
                    resolved = primary;
                    substituted = true;
                }
            }

            return new LegacyDependencyResolution(id, resolved, substituted);
        }).ToArray();
    }

    /// <summary>Reports missing state using the legacy vacuous-success behavior for unresolved dependencies.</summary>
    public static bool HasMissingDependencies(IReadOnlyList<LegacyDependencyResolution> resolutions) =>
        !resolutions.Where(resolution => resolution.Mod is not null).Select(resolution => resolution.Mod!).All(mod =>
            mod.WorkshopId != 0 &&
            mod.IsActive &&
            !mod.State.HasFlag(LegacyModState.NotInstalled) &&
            !mod.State.HasFlag(LegacyModState.NotLoaded));

    /// <summary>Calculates duplicate roles without performing descriptor renames.</summary>
    public static IReadOnlyList<LegacyDuplicateDecision> CalculateDuplicatePlan(IReadOnlyList<LegacyModFact> mods, bool workaroundEnabled)
    {
        var decisions = new List<LegacyDuplicateDecision>();
        foreach (var group in mods.GroupBy(mod => mod.ModId, StringComparer.InvariantCultureIgnoreCase).Where(group => group.Count() > 1))
        {
            if (!workaroundEnabled || !group.Any(mod => mod.DescriptorDisabled))
            {
                decisions.AddRange(group.Select(mod => new LegacyDuplicateDecision(mod.Key, LegacyDuplicateRole.Unresolved, false)));
                continue;
            }

            var primaryAssigned = false;
            foreach (var mod in group.OrderBy(mod => mod.DateAdded))
            {
                if (mod.DescriptorDisabled)
                {
                    decisions.Add(new LegacyDuplicateDecision(mod.Key, LegacyDuplicateRole.Disabled, false));
                }
                else if (!primaryAssigned)
                {
                    decisions.Add(new LegacyDuplicateDecision(mod.Key, LegacyDuplicateRole.Primary, false));
                    primaryAssigned = true;
                }
                else
                {
                    decisions.Add(new LegacyDuplicateDecision(mod.Key, LegacyDuplicateRole.Disabled, true));
                }
            }
        }

        return decisions;
    }
}
