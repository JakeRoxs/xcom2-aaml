using AAML.Application.Common;
using AAML.Application.Mods.Workshop;
using AAML.Application.Ports;
using AAML.Application.Settings;
using AAML.Domain.Games;
using AAML.Domain.Mods;
using FluentAssertions;

namespace AAML.Application.Tests;

[TestClass]
public sealed class WorkshopSubscriptionCoordinatorTests
{
    [TestMethod]
    public async Task Subscribe_ReturnsStructuredPartialOutcomesAndRequestsDownloadOnlyOnSuccess()
    {
        var workshop = new FakeWorkshop { FailureId = 2 }; var coordinator = new WorkshopSubscriptionCoordinator(workshop, new Repository());
        var result = await coordinator.SubscribeAsync([new WorkshopId(2), new WorkshopId(1)], TestContext.CancellationToken);
        result.IsPartialSuccess.Should().BeTrue();
        result.Items.Select(item => item.WorkshopId.Value).Should().Equal(1UL, 2UL);
        workshop.Downloads.Should().Equal(1UL);
        result.Items[0].Subscribed.Should().BeTrue();
        result.Items[0].DownloadRequested.Should().BeTrue();
        result.Items[1].Subscribed.Should().BeFalse();
    }

    [TestMethod]
    public async Task Subscribe_DistinguishesSuccessfulSubscriptionFromDownloadRequestFailure()
    {
        var workshop = new FakeWorkshop { DownloadFailureId = 1 };
        var result = await new WorkshopSubscriptionCoordinator(workshop, new Repository()).SubscribeAsync([new WorkshopId(1)], TestContext.CancellationToken);

        result.IsSuccess.Should().BeFalse();
        result.Items.Single().Subscribed.Should().BeTrue();
        result.Items.Single().DownloadRequested.Should().BeFalse();
        result.Items.Single().DownloadRequestOutcome!.Value.Error!.Code.Should().Be("fixture.download");
    }

    [TestMethod]
    public async Task RemoveRetainedIntent_RejectsAbsentIntentWithoutSaving()
    {
        var repository = new Repository();
        var settings = new ApplicationSettings(ApplicationSettingsDefaults.CurrentSchemaVersion, GameVariant.XCom2, null, [], [], [], [], []);

        var result = await new WorkshopSubscriptionCoordinator(new FakeWorkshop(), repository).RemoveRetainedIntentAsync(settings, new WorkshopId(99), TestContext.CancellationToken);

        result.Error!.Code.Should().Be("workshop.retained_intent_missing");
        repository.SaveCalls.Should().Be(0);
    }

    [TestMethod]
    public async Task UnsubscribeRetainAndRemoveIntent_AreDistinctAtomicSettingsOperations()
    {
        var workshop = new FakeWorkshop(); var repository = new Repository(); var coordinator = new WorkshopSubscriptionCoordinator(workshop, repository);
        var mod = new ModInstallation(new ModKey(ModSource.SteamWorkshop, "C:\\Workshop\\1"), new PackageId("Package"), "Name", new WorkshopId(1), false, DescriptorState.Enabled, null);
        var intent = new ModUserIntent(mod.Key, true, false, 0, null, null, new HashSet<TagId>(), null, new HashSet<WorkshopId>());
        var settings = new ApplicationSettings(ApplicationSettingsDefaults.CurrentSchemaVersion, GameVariant.XCom2, null, [], [], [intent], [], []);
        var retained = await coordinator.UnsubscribeRetainingIntentAsync(settings, [mod], new HashSet<ModKey> { mod.Key }, TestContext.CancellationToken);
        retained.Value!.Settings.RetainedWorkshopItems.Should().ContainSingle(item => item.WorkshopId == new WorkshopId(1));
        retained.Value.Settings.ModIntents.Should().ContainSingle();
        var removed = await coordinator.RemoveRetainedIntentAsync(retained.Value.Settings, new WorkshopId(1), TestContext.CancellationToken);
        removed.Value!.RetainedWorkshopItems.Should().BeEmpty(); removed.Value.ModIntents.Should().BeEmpty();
    }

    public TestContext TestContext { get; set; }
    private sealed class FakeWorkshop : IWorkshopService
    {
        public ulong? FailureId { get; set; }
        public ulong? DownloadFailureId { get; set; }
        public List<ulong> Downloads { get; } = [];
        public Task<Result> SubscribeAsync(WorkshopId id, CancellationToken token) => Task.FromResult(id.Value == FailureId ? Result.Failure(new Error("fixture", "Failed", ErrorKind.ExternalService)) : Result.Success());
        public Task<Result> UnsubscribeAsync(WorkshopId id, CancellationToken token) => Task.FromResult(Result.Success());
        public Task<Result> RequestDownloadAsync(WorkshopId id, bool priority, CancellationToken token) { Downloads.Add(id.Value); return Task.FromResult(id.Value == DownloadFailureId ? Result.Failure(new Error("fixture.download", "Download failed", ErrorKind.ExternalService)) : Result.Success()); }
        public Task<Result<WorkshopItem?>> GetItemAsync(WorkshopId id, CancellationToken token) => throw new NotSupportedException(); public Task<Result<IReadOnlyList<WorkshopItem>>> GetItemsAsync(IReadOnlyList<WorkshopId> ids, IProgress<OperationProgress>? progress, CancellationToken token) => throw new NotSupportedException(); public Task<Result<IReadOnlyList<WorkshopId>>> GetSubscribedItemsAsync(CancellationToken token) => throw new NotSupportedException(); public Task<Result<WorkshopLocalState>> GetLocalStateAsync(WorkshopId id, CancellationToken token) => throw new NotSupportedException(); public Task<Result<string?>> GetPersonaNameAsync(ulong id, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class Repository : ISettingsRepository { public int SaveCalls { get; private set; } public Task<Result<ApplicationSettings>> LoadAsync(CancellationToken token) => throw new NotSupportedException(); public Task<Result> SaveAsync(ApplicationSettings settings, CancellationToken token) { SaveCalls++; return Task.FromResult(Result.Success()); } }
}
