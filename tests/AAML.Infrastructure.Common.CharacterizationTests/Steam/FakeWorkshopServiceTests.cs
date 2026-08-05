using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Domain.Mods;
using FluentAssertions;

namespace AAML.Infrastructure.Common.CharacterizationTests.Steam;

[TestClass]
public sealed class FakeWorkshopServiceTests
{
    [TestMethod]
    public async Task ApplicationContract_RequiresNoSteamRuntime()
    {
        IWorkshopService service = new FakeWorkshopService();

        var result = await service.GetItemAsync(new WorkshopId(900000001), TestContext.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(new WorkshopItem(new WorkshopId(900000001), "Synthetic Workshop Mod", [new WorkshopId(900000002), new WorkshopId(900000003)]));
    }

    public TestContext TestContext { get; set; }

    private sealed class FakeWorkshopService : IWorkshopService
    {
        public Task<Result<WorkshopItem?>> GetItemAsync(WorkshopId publishedFileId, CancellationToken cancellationToken) =>
            Task.FromResult(Result<WorkshopItem?>.Success(new WorkshopItem(publishedFileId, "Synthetic Workshop Mod", [new WorkshopId(900000002), new WorkshopId(900000003)])));

        public Task<Result<IReadOnlyList<WorkshopItem>>> GetItemsAsync(IReadOnlyList<WorkshopId> publishedFileIds, IProgress<OperationProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(Result<IReadOnlyList<WorkshopItem>>.Success([]));

        public Task<Result<IReadOnlyList<WorkshopId>>> GetSubscribedItemsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result<IReadOnlyList<WorkshopId>>.Success([]));

        public Task<Result<WorkshopLocalState>> GetLocalStateAsync(WorkshopId publishedFileId, CancellationToken cancellationToken) =>
            Task.FromResult(Result<WorkshopLocalState>.Success(new WorkshopLocalState(publishedFileId, WorkshopItemState.None, null, null)));

        public Task<Result> RequestDownloadAsync(WorkshopId publishedFileId, bool highPriority, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result<string?>> GetPersonaNameAsync(ulong steamId, CancellationToken cancellationToken) =>
            Task.FromResult(Result<string?>.Success("Synthetic User"));
    }
}
