using AAML.Infrastructure.Common.Compatibility.Mods;
using FluentAssertions;

namespace AAML.Infrastructure.Common.CharacterizationTests.Mods;

[TestClass]
public sealed class LegacyModAnalysisTests
{
    [TestMethod]
    public void Dependencies_SubstituteExactCasePrimaryAndSkipIgnoredIds()
    {
        var owner = Mod("owner", "Owner", 1, dependencies: [10, 20], ignored: new HashSet<long> { 20 });
        var disabled = Mod("disabled", "Shared", 10, state: LegacyModState.DuplicateDisabled);
        var primary = Mod("primary", "Shared", 99, state: LegacyModState.DuplicatePrimary);

        var result = LegacyModAnalysis.ResolveDependencies(owner, [disabled, primary]);

        result.Should().ContainSingle();
        result[0].Mod.Should().Be(primary);
        result[0].Substituted.Should().BeTrue();
    }

    [TestMethod]
    public void LegacyUnresolvedDependency_VacuouslyAppearsAvailable()
    {
        var owner = Mod("owner", "Owner", 1, dependencies: [404]);
        var resolutions = LegacyModAnalysis.ResolveDependencies(owner, []);

        LegacyModAnalysis.HasMissingDependencies(resolutions).Should().BeFalse();
    }

    [TestMethod]
    public void InactiveInstalledDependency_IsMissing()
    {
        var owner = Mod("owner", "Owner", 1, dependencies: [10]);
        var inactive = Mod("dependency", "Dependency", 10, active: false);

        var resolutions = LegacyModAnalysis.ResolveDependencies(owner, [inactive]);

        LegacyModAnalysis.HasMissingDependencies(resolutions).Should().BeTrue();
    }

    [TestMethod]
    public void DuplicatePlan_UsesDisabledPresenceAndEarliestEnabledPrimary()
    {
        var later = Mod("later", "Duplicate", 1, date: new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero));
        var disabled = Mod("disabled", "duplicate", 2, disabled: true);
        var earlier = Mod("earlier", "DUPLICATE", 3, date: new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var result = LegacyModAnalysis.CalculateDuplicatePlan([later, disabled, earlier], true);

        result.Should().ContainEquivalentOf(new LegacyDuplicateDecision("earlier", LegacyDuplicateRole.Primary, false));
        result.Should().ContainEquivalentOf(new LegacyDuplicateDecision("later", LegacyDuplicateRole.Disabled, true));
        result.Should().ContainEquivalentOf(new LegacyDuplicateDecision("disabled", LegacyDuplicateRole.Disabled, false));
    }

    private static LegacyModFact Mod(
        string key,
        string modId,
        long workshopId,
        bool active = true,
        LegacyModState state = LegacyModState.None,
        IReadOnlyList<long>? dependencies = null,
        IReadOnlySet<long>? ignored = null,
        DateTimeOffset? date = null,
        bool disabled = false) =>
        new(key, modId, workshopId, active, state, dependencies ?? [], ignored ?? new HashSet<long>(), date, disabled);
}
