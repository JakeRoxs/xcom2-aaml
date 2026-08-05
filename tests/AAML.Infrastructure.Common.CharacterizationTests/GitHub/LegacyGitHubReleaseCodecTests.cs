using AAML.Infrastructure.Common.Compatibility.GitHub;
using FluentAssertions;

namespace AAML.Infrastructure.Common.CharacterizationTests.GitHub;

[TestClass]
public sealed class LegacyGitHubReleaseCodecTests
{
    [TestMethod]
    public void ReleaseFixture_MapsUpdaterFields()
    {
        var json = CompatibilityFixture.Read("http", "github-release.json");

        var result = LegacyGitHubReleaseCodec.Parse(json);

        result.TagName.Should().Be("v9.9.9");
        result.Draft.Should().BeFalse();
        result.Prerelease.Should().BeFalse();
        result.Assets.Should().ContainSingle().Which.Name.Should().Be("Synthetic_9.9.9.zip");
        result.Assets[0].DownloadUrl.Should().Be(new Uri("https://example.invalid/Synthetic_9.9.9.zip"));
    }

    [TestMethod]
    public void EmptyReleaseList_SelectsNothing()
    {
        var json = CompatibilityFixture.Read("http", "github-releases-empty.json");

        var result = LegacyGitHubReleaseCodec.ParseFirst(json);

        result.Should().BeNull();
    }
}
