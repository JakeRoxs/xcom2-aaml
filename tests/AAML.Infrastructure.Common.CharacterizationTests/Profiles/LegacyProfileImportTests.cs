using AAML.Application.Profiles;
using AAML.Application.Settings;
using AAML.Domain.Games;
using AAML.Domain.Launching;
using AAML.Infrastructure.Common.Compatibility.Profiles;
using AAML.Infrastructure.Common.Files;
using AAML.Infrastructure.Common.Profiles;
using FluentAssertions;

namespace AAML.Infrastructure.Common.CharacterizationTests.Profiles;

[TestClass]
public sealed class LegacyProfileImportTests
{
    [TestMethod]
    public async Task GroupedCorpus_ImportsPortableProfileAndRepeatedImportIsNoOp()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML Legacy Profiles", Guid.NewGuid().ToString("N"));
        var repository = new JsonProfileRepository(new TestPaths(root), new AtomicTextWriter());
        var service = new LegacyProfileImportService(new LegacyProfileParser(), repository);
        var settings = Settings();
        try
        {
            var document = CompatibilityFixture.Read("profiles", "grouped-current.txt");

            var first = await service.ImportAsync("Legacy Campaign", document, settings, TestContext.CancellationToken);
            var second = await service.ImportAsync("Renamed input is ignored for identity", document, settings, TestContext.CancellationToken);

            first.Value!.Imported.Should().BeTrue();
            first.Value.Profile.GameVariant.Should().Be(GameVariant.XCom2WarOfTheChosen);
            first.Value.Profile.Mods.Select(mod => mod.PackageId.Value).Should().Equal("SyntheticA", "SyntheticB", "LocalSynthetic");
            first.Value.Profile.Mods.Take(2).Select(mod => mod.WorkshopId!.Value.Value).Should().Equal(900000001UL, 900000002UL);
            first.Value.Profile.Mods[2].WorkshopId.Should().BeNull();
            first.Value.Profile.Mods[2].Source.Should().Be(AAML.Domain.Mods.ModSource.Manual);
            first.Value.Diagnostics.Should().BeEmpty();
            second.Value!.Imported.Should().BeFalse();
            second.Value.Profile.Id.Should().Be(first.Value.Profile.Id);
            (await repository.ListAsync(TestContext.CancellationToken)).Value.Should().ContainSingle();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task UngroupedCorpus_ImportsSameOrderedWorkshopSet()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML Legacy Profiles", Guid.NewGuid().ToString("N"));
        var repository = new JsonProfileRepository(new TestPaths(root), new AtomicTextWriter());
        try
        {
            var result = await new LegacyProfileImportService(new LegacyProfileParser(), repository)
                .ImportAsync("Ungrouped", CompatibilityFixture.Read("profiles", "ungrouped-current.txt"), Settings(), TestContext.CancellationToken);

            result.Value!.Profile.Mods.Select(mod => mod.Order).Should().Equal(0, 1);
            result.Value.Diagnostics.Should().BeEmpty();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ProfileMetadataImport_RoundTripsAndReimportsIdempotently()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML Legacy Profile Metadata", Guid.NewGuid().ToString("N"));
        var repository = new JsonProfileRepository(new TestPaths(root), new AtomicTextWriter());
        var service = new LegacyProfileImportService(new LegacyProfileParser(), repository);
        try
        {
            var preview = service.Preview(CompatibilityFixture.Read("profiles", "grouped-current.txt")).Value!;
            var first = await service.ImportAsync("Metadata", preview, LegacyTaxonomyDisposition.ProfileMetadata, Settings(), [], TestContext.CancellationToken);
            var second = await service.ImportAsync("Metadata again", preview, LegacyTaxonomyDisposition.ProfileMetadata, Settings(), [], TestContext.CancellationToken);
            first.Value!.Profile.LegacyMetadata!.Rows[0].Category.Should().Be("Gameplay");
            second.Value!.Imported.Should().BeFalse();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public TestContext TestContext { get; set; }

    private static ApplicationSettings Settings() => new(
        ApplicationSettingsDefaults.CurrentSchemaVersion,
        GameVariant.XCom2WarOfTheChosen,
        "C:\\Game",
        [],
        [new LaunchArgument("-review"), new LaunchArgument("-noRedScreens")],
        [], [], []);

    private sealed class TestPaths(string root) : AAML.Application.Ports.IApplicationPaths
    {
        public string ConfigurationDirectory { get; } = Path.Combine(root, "Config");
        public string DataDirectory { get; } = Path.Combine(root, "Data");
        public string StateDirectory { get; } = Path.Combine(root, "State");
        public string CacheDirectory { get; } = Path.Combine(root, "Cache");
        public string RuntimeDirectory { get; } = Path.Combine(root, "Runtime");
    }
}
