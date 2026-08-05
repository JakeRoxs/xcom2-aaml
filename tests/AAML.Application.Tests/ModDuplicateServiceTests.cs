using AAML.Application.Mods.Duplicates;
using AAML.Application.Mods;
using AAML.Application.Ports;
using AAML.Application.Settings;
using AAML.Domain.Games;
using AAML.Domain.Mods;
using FluentAssertions;

namespace AAML.Application.Tests;

[TestClass]
public sealed class ModDuplicateServiceTests
{
    [TestMethod]
    public void Analyze_IsCaseInsensitiveDeterministicAndRequiresExplicitPreference()
    {
        var a = Mod("C:\\A", "Shared"); var b = Mod("C:\\B", "shared");
        var analyzer = new ModDuplicateAnalyzer();
        var unresolved = analyzer.Analyze([b, a], []);
        var resolved = analyzer.Analyze([a, b], [new DuplicatePreference(new PackageId("SHARED"), b.Key)]);

        unresolved.Groups.Should().ContainSingle();
        unresolved.Statuses.Values.Should().OnlyContain(status => status == DuplicateStatus.Unresolved);
        resolved.Status(a.Key).Should().Be(DuplicateStatus.Secondary);
        resolved.Status(b.Key).Should().Be(DuplicateStatus.Preferred);
        resolved.Groups.Single().Installations.Select(mod => mod.Key).Should().Equal(a.Key, b.Key);
    }

    [TestMethod]
    public void Analyze_StaleAndDisabledPreferencesRemainUnresolved()
    {
        var a = Mod("C:\\A", "Shared"); var b = Mod("C:\\B", "Shared") with { DescriptorState = DescriptorState.Disabled };
        var analyzer = new ModDuplicateAnalyzer();
        analyzer.Analyze([a, b], [new DuplicatePreference(a.PackageId, new ModKey(ModSource.Manual, "C:\\Missing"))]).Groups.Single().IsResolved.Should().BeFalse();
        analyzer.Analyze([a, b], [new DuplicatePreference(a.PackageId, b.Key)]).Groups.Single().Issue.Should().Contain("disabled");
    }

    [TestMethod]
    public async Task Prefer_TransfersActivationWithoutMutatingMetadata()
    {
        var a = Mod("C:\\A", "Shared"); var b = Mod("C:\\B", "Shared");
        var original = new ModUserIntent(a.Key, true, true, 4, "Manual", null, new HashSet<TagId>(), "Note", new HashSet<WorkshopId>());
        var repository = new RecordingRepository();
        var settings = Settings([original]);

        var result = await new DuplicatePreferenceService(repository).PreferAsync(settings, [a, b], b.Key, TestContext.CancellationToken);

        result.Value!.DuplicatePreferences.Should().ContainSingle(preference => preference.PreferredInstallation == b.Key);
        result.Value.ModIntents.Single(intent => intent.Mod == a.Key).Should().BeEquivalentTo(original with { IsActive = false });
        result.Value.ModIntents.Single(intent => intent.Mod == b.Key).IsActive.Should().BeTrue();
        result.Value.ModIntents.Single(intent => intent.Mod == b.Key).ExplicitOrder.Should().Be(4);
    }

    [TestMethod]
    public void ActivationPolicy_BlocksUnresolvedAndSecondaryActiveGroups()
    {
        var a = Mod("C:\\A", "Shared"); var b = Mod("C:\\B", "Shared"); var analyzer = new ModDuplicateAnalyzer();
        var unresolved = analyzer.Analyze([a, b], []);
        ModDuplicateActivationPolicy.Validate([a, b], [new ModIntentEdit(a.Key, true, 0)], unresolved).Error!.Code.Should().Be("duplicates.unresolved_active");
        var resolved = analyzer.Analyze([a, b], [new DuplicatePreference(a.PackageId, b.Key)]);
        ModDuplicateActivationPolicy.Validate([a, b], [new ModIntentEdit(a.Key, true, 0)], resolved).Error!.Code.Should().Be("duplicates.invalid_active");
        ModDuplicateActivationPolicy.Validate([a, b], [new ModIntentEdit(b.Key, true, 0)], resolved).IsSuccess.Should().BeTrue();
    }

    public TestContext TestContext { get; set; }
    private static ModInstallation Mod(string path, string package) => new(new ModKey(ModSource.Manual, path), new PackageId(package), package, null, false, DescriptorState.Enabled, null);
    private static ApplicationSettings Settings(IReadOnlyList<ModUserIntent> intents) => new(ApplicationSettingsDefaults.CurrentSchemaVersion, GameVariant.XCom2, null, [], [], intents, [], []);
    private sealed class RecordingRepository : ISettingsRepository
    {
        public Task<AAML.Application.Common.Result<ApplicationSettings>> LoadAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AAML.Application.Common.Result> SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken) => Task.FromResult(AAML.Application.Common.Result.Success());
    }
}
