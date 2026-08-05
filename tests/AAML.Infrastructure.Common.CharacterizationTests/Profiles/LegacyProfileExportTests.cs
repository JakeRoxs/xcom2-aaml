using AAML.Application.Profiles;
using AAML.Domain.Games;
using AAML.Domain.Mods;
using AAML.Domain.Profiles;
using AAML.Infrastructure.Common.Compatibility.Profiles;
using FluentAssertions;

namespace AAML.Infrastructure.Common.CharacterizationTests.Profiles;

[TestClass]
public sealed class LegacyProfileExportTests
{
    [TestMethod]
    public void Export_PreservesRepresentableTaxonomyAndManualRows()
    {
        var profile = new ModProfile(new ProfileId(Guid.NewGuid()), "Legacy", GameVariant.XCom2, [
            new(ModSource.SteamWorkshop, new PackageId("Workshop"), new WorkshopId(42), 0),
            new(ModSource.Manual, new PackageId("Local"), null, 1)], [], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            new LegacyProfileMetadata("source", [new(0, "Workshop Name", "Gameplay", ["Stable"], 2), new(1, "Local Name", "Local", ["Manual"], 5)]));

        var exported = new LegacyProfileExportService().Export(profile, new(true, true, LegacyWorkshopIdStyle.Url)).Value!;
        var parsed = new LegacyProfileParser().Parse(exported.Contents).Value!;

        parsed.Entries.Select(entry => entry.PackageId).Should().Equal("Workshop", "Local");
        parsed.Entries[0].Category.Should().Be("Gameplay");
        parsed.Entries[0].Tags.Should().Equal("Stable");
        parsed.Entries[1].Source.Should().Be(ModSource.Manual);
        exported.Diagnostics.Should().Contain(message => message.StartsWith("legacy_export.game_omitted"));
    }
}
