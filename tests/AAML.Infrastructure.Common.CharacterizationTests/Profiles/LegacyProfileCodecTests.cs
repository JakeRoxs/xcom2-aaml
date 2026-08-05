using AAML.Infrastructure.Common.Compatibility.Profiles;
using FluentAssertions;

namespace AAML.Infrastructure.Common.CharacterizationTests.Profiles;

[TestClass]
public sealed class LegacyProfileCodecTests
{
    [TestMethod]
    public void GroupedProfile_ParsesCategoriesTagsAndSteamUrl()
    {
        var profile = CompatibilityFixture.Read("profiles", "grouped-current.txt");

        var result = LegacyProfileCodec.Parse(profile);

        result.Should().HaveCount(2);
        result[0].Should().BeEquivalentTo(new LegacyProfileEntry("Synthetic Mod A", "SyntheticA", 900000001, "Gameplay", ["Stable", "Campaign"]));
        result[1].SourceId.Should().Be(900000002);
        result[1].Category.Should().Be("Gameplay");
    }

    [TestMethod]
    public void LegacyImporter_SkipsUnknownLocalSource()
    {
        var profile = CompatibilityFixture.Read("profiles", "grouped-current.txt");

        var result = LegacyProfileCodec.Parse(profile);

        result.Should().NotContain(entry => entry.ModId == "LocalSynthetic");
    }
}
