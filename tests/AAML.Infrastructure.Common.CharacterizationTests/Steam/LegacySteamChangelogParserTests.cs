using AAML.Infrastructure.Common.Compatibility.Steam;
using FluentAssertions;

namespace AAML.Infrastructure.Common.CharacterizationTests.Steam;

[TestClass]
public sealed class LegacySteamChangelogParserTests
{
    [TestMethod]
    public void ChangelogFixture_ProducesLegacyWindowsText()
    {
        var html = CompatibilityFixture.Read("http", "steam-changelog.html");

        var result = LegacySteamChangelogParser.Parse(html);

        result.Should().Be("17 Jul, 2026 @ 12:34pm\r\n\tSynthetic change one\r\n\tSynthetic change two & more\r\n\r\n");
    }

    [TestMethod]
    public void ChangedClassOrdering_IsNotRecognized()
    {
        const string html = "<div class=\"changeLogCtn detailBox workshopAnnouncement noFooter\"><div class=\"changelog headline\">Date</div><p id=\"1\">Text</p></div>";

        var result = LegacySteamChangelogParser.Parse(html);

        result.Should().BeEmpty();
    }
}
