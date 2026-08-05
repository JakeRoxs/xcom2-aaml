using AAML.Infrastructure.Common.Compatibility.Mods;
using FluentAssertions;

namespace AAML.Infrastructure.Common.CharacterizationTests.Mods;

[TestClass]
public sealed class LegacyConflictAnalysisTests
{
    [TestMethod]
    public void DistinctActiveClassOverrides_ConflictAcrossOldClassCasing()
    {
        var facts = new[]
        {
            Fact("a", "ModA", "FixtureBase", "ReplacementA", LegacyOverrideKind.Class, "+Override=A"),
            Fact("b", "ModB", "fixturebase", "ReplacementB", LegacyOverrideKind.Class, "+Override=B")
        };

        var result = LegacyConflictAnalysis.Calculate(facts);

        result.Should().ContainSingle().Which.Overrides.Should().HaveCount(2);
    }

    [TestMethod]
    public void UiListenersAlone_DoNotConflict()
    {
        var facts = new[]
        {
            Fact("a", "ModA", "Screen", "ListenerA", LegacyOverrideKind.UiScreenListener, "ScreenClass=A"),
            Fact("b", "ModB", "Screen", "ListenerB", LegacyOverrideKind.UiScreenListener, "ScreenClass=B")
        };

        LegacyConflictAnalysis.Calculate(facts).Should().BeEmpty();
    }

    [TestMethod]
    public void IdenticalOriginalLines_SuppressConflict()
    {
        var facts = new[]
        {
            Fact("a", "ModA", "Base", "A", LegacyOverrideKind.Class, "+Same"),
            Fact("b", "ModB", "Base", "B", LegacyOverrideKind.Class, "+Same")
        };

        LegacyConflictAnalysis.Calculate(facts).Should().BeEmpty();
    }

    private static LegacyOverrideFact Fact(string key, string modId, string oldClass, string newClass, LegacyOverrideKind kind, string line) =>
        new(key, modId, true, oldClass, newClass, kind, line);
}
