using AAML.Infrastructure.Common.Compatibility.Mods;
using FluentAssertions;

namespace AAML.Infrastructure.Common.CharacterizationTests.Mods;

[TestClass]
public sealed class LegacyModMetadataParserTests
{
    [TestMethod]
    public void ManualDescriptor_ParsesLegacyContinuationAndExpansionFlag()
    {
        var descriptor = CompatibilityFixture.Read("mods", "manual", "SyntheticMod", "SyntheticMod.XComMod");

        var result = LegacyModMetadataParser.Parse(descriptor);

        result.PublishedFileId.Should().Be(900000001);
        result.Title.Should().Be("Synthetic Compatibility Mod");
        result.Category.Should().Be("Compatibility Fixtures");
        result.Description.Should().Be("First synthetic line\r\nsecond synthetic line");
        result.Tags.Should().Be("Strategy,Test");
        result.RequiresExpansion.Should().BeTrue();
        result.ContentImage.Should().Be("ModPreview.jpg");
    }

    [TestMethod]
    public void DisabledDescriptor_UsesUnsortedCategory()
    {
        var descriptor = CompatibilityFixture.Read("mods", "disabled", "DisabledSynthetic.XComMod-disabled");

        var result = LegacyModMetadataParser.Parse(descriptor);

        result.PublishedFileId.Should().Be(900000003);
        result.Category.Should().Be("Unsorted");
    }
}
