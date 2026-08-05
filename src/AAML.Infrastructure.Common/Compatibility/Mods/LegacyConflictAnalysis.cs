namespace AAML.Infrastructure.Common.Compatibility.Mods;

/// <summary>Legacy class-override source kind.</summary>
public enum LegacyOverrideKind
{
    Class,
    UiScreenListener
}

/// <summary>A class override projected from one mod.</summary>
public sealed record LegacyOverrideFact(
    string ModKey,
    string ModId,
    bool IsActive,
    string OldClass,
    string NewClass,
    LegacyOverrideKind Kind,
    string OriginalLine);

/// <summary>A conflict between active class overrides.</summary>
public sealed record LegacyModConflict(string ClassName, IReadOnlyList<LegacyOverrideFact> Overrides);

/// <summary>Calculates conflicts according to the legacy predicates.</summary>
public static class LegacyConflictAnalysis
{
    /// <summary>Returns active conflicts grouped case-insensitively by replaced class.</summary>
    public static IReadOnlyList<LegacyModConflict> Calculate(IEnumerable<LegacyOverrideFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var active = facts.Where(fact => fact.IsActive).ToArray();
        return active
            .GroupBy(fact => fact.OldClass, StringComparer.InvariantCultureIgnoreCase)
            .Where(group =>
                group.Count() > 1 &&
                group.Any(item => item.Kind == LegacyOverrideKind.Class) &&
                group.Any(item => item.ModId != group.First().ModId) &&
                group.Select(item => item.OriginalLine).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(group => new LegacyModConflict(group.Key, group.ToArray()))
            .ToArray();
    }
}
