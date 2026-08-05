using AAML.Application.Common;
using AAML.Application.Mods;
using AAML.Application.Ports;
using AAML.Application.Settings;
using AAML.Domain.Games;
using AAML.Domain.Mods;
using FluentAssertions;

namespace AAML.Application.Tests;

[TestClass]
public sealed class ModIntentServiceTests
{
    [TestMethod]
    public async Task IgnoreAndUnignoreDependency_PreserveMetadataAndCollapseEmptyIntent()
    {
        var repository = new RecordingRepository();
        var service = new ModIntentService(repository);
        var mod = new ModKey(ModSource.Manual, "C:\\Mod");
        var settings = new ApplicationSettings(ApplicationSettingsDefaults.CurrentSchemaVersion, GameVariant.XCom2, null, [], [], [], [], []);

        var ignored = await service.SetDependencyIgnoredAsync(settings, mod, new WorkshopId(42), true, TestContext.CancellationToken);
        var restored = await service.SetDependencyIgnoredAsync(ignored.Value!, mod, new WorkshopId(42), false, TestContext.CancellationToken);

        ignored.Value!.ModIntents.Single().IgnoredDependencies.Should().Contain(new WorkshopId(42));
        restored.Value!.ModIntents.Should().BeEmpty();
    }
    [TestMethod]
    public async Task GridEdits_PreserveNonGridIntentAndCreateOnlyMeaningfulNewIntents()
    {
        var existingKey = Key("Existing");
        var inactiveKey = Key("UnchangedInactive");
        var activatedKey = Key("Activated");
        var existing = new ModUserIntent(existingKey, true, true, 7, "Custom", new CategoryId("gameplay"), new HashSet<TagId> { new("stable") }, "Note", new HashSet<WorkshopId> { new(123) });
        var settings = new ApplicationSettings(ApplicationSettingsDefaults.CurrentSchemaVersion, GameVariant.XCom2WarOfTheChosen, "C:\\Game", [], ApplicationSettingsDefaults.LaunchArguments, [existing], [], [], false);
        var repository = new RecordingRepository();

        var result = await new ModIntentService(repository).SaveAsync(settings,
            [new ModIntentEdit(existingKey, false, 3), new ModIntentEdit(inactiveKey, false, null), new ModIntentEdit(activatedKey, true, 1)], TestContext.CancellationToken);

        result.Value!.ModIntents.Should().HaveCount(2);
        result.Value.ModIntents.Single(intent => intent.Mod == existingKey).Should().BeEquivalentTo(existing with { IsActive = false, ExplicitOrder = 3 });
        result.Value.ModIntents.Single(intent => intent.Mod == activatedKey).IsActive.Should().BeTrue();
        repository.Saved.Should().Be(result.Value);
    }

    [TestMethod]
    public async Task NegativeOrder_FailsWithoutPersisting()
    {
        var repository = new RecordingRepository();
        var settings = new ApplicationSettings(ApplicationSettingsDefaults.CurrentSchemaVersion, GameVariant.XCom2, null, [], [], [], [], [], false);

        var result = await new ModIntentService(repository).SaveAsync(settings, [new ModIntentEdit(Key("Bad"), true, -1)], TestContext.CancellationToken);

        result.Error!.Code.Should().Be("mods.order_invalid");
        repository.Saved.Should().BeNull();
    }

    [TestMethod]
    public async Task DeactivatingMetadataFreeIntent_RemovesRedundantPersistence()
    {
        var key = Key("Temporary");
        var intent = new ModUserIntent(key, true, false, null, null, null, new HashSet<TagId>(), null, new HashSet<WorkshopId>());
        var settings = new ApplicationSettings(ApplicationSettingsDefaults.CurrentSchemaVersion, GameVariant.XCom2, null, [], [], [intent], [], [], false);
        var repository = new RecordingRepository();

        var result = await new ModIntentService(repository).SaveAsync(settings, [new ModIntentEdit(key, false, null)], TestContext.CancellationToken);

        result.Value!.ModIntents.Should().BeEmpty();
    }

    [TestMethod]
    public void Merge_ProjectsEffectiveDraftWithoutPersisting()
    {
        var first = Key("First");
        var second = Key("Second");
        var settings = new ApplicationSettings(ApplicationSettingsDefaults.CurrentSchemaVersion, GameVariant.XCom2, null, [], [],
            [new ModUserIntent(first, true, false, 0, null, null, new HashSet<TagId>(), null, new HashSet<WorkshopId>())], [], [], false);
        var repository = new RecordingRepository();

        var result = new ModIntentService(repository).Merge(settings,
            [new ModIntentEdit(first, false, null), new ModIntentEdit(second, true, 0)]);

        result.Value!.ModIntents.Should().ContainSingle(intent => intent.Mod == second && intent.IsActive && intent.ExplicitOrder == 0);
        result.Value.ModIntents.Should().NotContain(intent => intent.Mod == first);
        settings.ModIntents.Should().ContainSingle(intent => intent.Mod == first && intent.IsActive);
        repository.Saved.Should().BeNull();
    }

    [TestMethod]
    public void Merge_InvalidDraftFailsWithoutPersisting()
    {
        var repository = new RecordingRepository();
        var settings = new ApplicationSettings(ApplicationSettingsDefaults.CurrentSchemaVersion, GameVariant.XCom2, null, [], [], [], [], [], false);

        var result = new ModIntentService(repository).Merge(settings, [new ModIntentEdit(Key("Bad"), true, -1)]);

        result.Error!.Code.Should().Be("mods.order_invalid");
        repository.Saved.Should().BeNull();
    }

    public TestContext TestContext { get; set; }
    private static ModKey Key(string name) => new(ModSource.Manual, $"C:\\Mods\\{name}");

    private sealed class RecordingRepository : ISettingsRepository
    {
        public ApplicationSettings? Saved { get; private set; }
        public Task<Result<ApplicationSettings>> LoadAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Result> SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken) { Saved = settings; return Task.FromResult(Result.Success()); }
    }
}
