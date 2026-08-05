using AAML.Application.Common;
using AAML.Application.Configurations;
using AAML.Application.Mods;
using AAML.Application.Ports;
using AAML.Application.Settings;
using AAML.Domain.Games;
using AAML.Domain.Mods;
using FluentAssertions;

namespace AAML.Application.Tests;

[TestClass]
public sealed class ActiveModImportServiceTests
{
    [TestMethod]
    public async Task PreviewAndReplace_ReportUnknownDuplicatesAndPreserveIntentMetadata()
    {
        var first = Mod("First"); var second = Mod("Second");
        var existing = new ModUserIntent(second.Key, true, true, 9, "Custom", new CategoryId("tools"), new HashSet<TagId> { new("stable") }, "note", new HashSet<WorkshopId>());
        var settings = new ApplicationSettings(ApplicationSettingsDefaults.CurrentSchemaVersion, GameVariant.XCom2, null, [], [], [existing], [], []);
        var repository = new RecordingRepository();
        var service = new ActiveModImportService(new ModIntentService(repository));
        var source = new ActiveModSource("XComModOptions.ini", "[Engine.XComModOptions]\nActiveMods=First\nActiveMods=Missing\nActiveMods=First\n", true);

        var preview = service.Preview(GameVariant.XCom2, ActiveModImportMode.Replace, [source], [first, second], settings).Value!;
        preview.Rows.Select(row => row.Resolution).Should().Equal(ActiveModResolution.Resolved, ActiveModResolution.Unknown, ActiveModResolution.Duplicate);
        var applied = await service.ApplyAsync(preview, [first, second], settings, TestContext.CancellationToken);

        applied.Value!.ModIntents.Single(intent => intent.Mod == first.Key).IsActive.Should().BeTrue();
        applied.Value.ModIntents.Single(intent => intent.Mod == second.Key).Should().BeEquivalentTo(existing with { IsActive = false, ExplicitOrder = null });
    }

    [TestMethod]
    public void AmbiguousPackage_UsesExistingDuplicatePreferenceOnly()
    {
        var first = Mod("Same", "One"); var second = Mod("Same", "Two");
        var settings = new ApplicationSettings(ApplicationSettingsDefaults.CurrentSchemaVersion, GameVariant.XCom2, null, [], [], [], [], [], DuplicatePreferences: [new DuplicatePreference(first.PackageId, second.Key)]);
        var service = new ActiveModImportService(new ModIntentService(new RecordingRepository()));

        var preview = service.Preview(GameVariant.XCom2, ActiveModImportMode.Merge, [new("source", "[Engine.XComModOptions]\nActiveMods=Same", true)], [first, second], settings);

        preview.Value!.Rows.Single().SelectedMod.Should().Be(second.Key);
        preview.Value.Rows.Single().Resolution.Should().Be(ActiveModResolution.Resolved);
    }

    public TestContext TestContext { get; set; }
    private static ModInstallation Mod(string package, string? location = null) => new(new ModKey(ModSource.Manual, $@"C:\Mods\{location ?? package}"), new PackageId(package), package, null, false, DescriptorState.Enabled, null);
    private sealed class RecordingRepository : ISettingsRepository
    {
        public Task<Result<ApplicationSettings>> LoadAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Result> SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
    }
}
