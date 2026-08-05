using AAML.Infrastructure.Common.Compatibility.Settings;
using FluentAssertions;

namespace AAML.Infrastructure.Common.CharacterizationTests.Settings;

[TestClass]
public sealed class LegacySettingsArgumentsTests
{
    [TestMethod]
    public void HistoricalString_PreservesOrderSpellingAndDistinctValues()
    {
        var json = CompatibilityFixture.Read("settings", "historical-string-arguments.json");

        var result = LegacySettingsArguments.Read(json);

        result.Should().Equal("-review", "-noRedscreens", "-log");
    }

    [TestMethod]
    public void HistoricalObject_IsReplacedByLegacyDefaults()
    {
        var json = CompatibilityFixture.Read("settings", "historical-object-arguments.json");

        var result = LegacySettingsArguments.Read(json);

        result.Should().Equal("-review", "-noRedScreens");
    }

    [TestMethod]
    public void CurrentArguments_AreReadWithoutMigration()
    {
        var json = CompatibilityFixture.Read("settings", "current-xcom2.json");

        var result = LegacySettingsArguments.Read(json);

        result.Should().Equal("-review", "-noRedscreens", "-log");
    }
}
