using AAML.Application.Common;
using AAML.Application.Mods.Dependencies;
using AAML.Application.Ports;
using AAML.Domain.Mods;
using FluentAssertions;

namespace AAML.Application.Tests;

[TestClass]
public sealed class ModDependencyServiceTests
{
    [TestMethod]
    public async Task Graph_ClassifiesMissingInactiveIgnoredAndCycleDeterministically()
    {
        var a = Id(1); var b = Id(2); var c = Id(3); var d = Id(4);
        var workshop = new FakeWorkshop(
            new WorkshopItem(a, "A", [b, c, d]),
            new WorkshopItem(b, "B", [a]),
            new WorkshopItem(c, "C", []),
            new WorkshopItem(d, "D", []));
        var service = new ModDependencyService(workshop);

        var result = await service.EvaluateAsync([a], [a, b, d], [a, b], new Dictionary<WorkshopId, IReadOnlySet<WorkshopId>> { [a] = new HashSet<WorkshopId> { d } }, TestContext.CancellationToken);

        result.Value!.Issues.Select(issue => issue.Kind).Should().BeEquivalentTo([ModDependencyIssueKind.Cyclic, ModDependencyIssueKind.Missing, ModDependencyIssueKind.Ignored]);
        result.Value.HasBlockingIssues.Should().BeTrue();
        result.Value.Issues.Single(issue => issue.Kind == ModDependencyIssueKind.Cyclic).Path.Should().Equal(a, b, a);
    }

    [TestMethod]
    public async Task InstalledButInactive_IsDistinctFromMissing()
    {
        var a = Id(1); var b = Id(2);
        var service = new ModDependencyService(new FakeWorkshop(new WorkshopItem(a, "A", [b]), new WorkshopItem(b, "B", [])));

        var result = await service.EvaluateAsync([a], [a, b], [a], new Dictionary<WorkshopId, IReadOnlySet<WorkshopId>>(), TestContext.CancellationToken);

        result.Value!.Issues.Should().ContainSingle(issue => issue.Kind == ModDependencyIssueKind.Inactive && issue.Required == b);
    }

    [TestMethod]
    public async Task SteamFailure_ProducesUnknownBlockingDiagnosticsInsteadOfSatisfied()
    {
        var service = new ModDependencyService(new FakeWorkshop(new Error("steam.offline", "Steam is offline.", ErrorKind.Unavailable)));

        var result = await service.EvaluateAsync([Id(1)], [Id(1)], [Id(1)], new Dictionary<WorkshopId, IReadOnlySet<WorkshopId>>(), TestContext.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value!.HasBlockingIssues.Should().BeTrue();
        result.Value.Issues.Should().ContainSingle(issue => issue.Kind == ModDependencyIssueKind.MetadataUnavailable);
    }

    [TestMethod]
    public async Task Metadata_IsCachedAcrossEvaluations()
    {
        var a = Id(1);
        var workshop = new FakeWorkshop(new WorkshopItem(a, "A", []));
        var service = new ModDependencyService(workshop);

        await service.EvaluateAsync([a], [a], [a], new Dictionary<WorkshopId, IReadOnlySet<WorkshopId>>(), TestContext.CancellationToken);
        await service.EvaluateAsync([a], [a], [a], new Dictionary<WorkshopId, IReadOnlySet<WorkshopId>>(), TestContext.CancellationToken);

        workshop.QueryCalls.Should().Be(1);
    }

    public TestContext TestContext { get; set; }
    private static WorkshopId Id(ulong value) => new(value);

    private sealed class FakeWorkshop : IWorkshopService
    {
        private readonly Dictionary<WorkshopId, WorkshopItem> items;
        private readonly Error? failure;
        public int QueryCalls { get; private set; }
        public FakeWorkshop(params WorkshopItem[] items) => this.items = items.ToDictionary(item => item.PublishedFileId);
        public FakeWorkshop(Error failure) { this.failure = failure; items = []; }
        public Task<Result<IReadOnlyList<WorkshopItem>>> GetItemsAsync(IReadOnlyList<WorkshopId> ids, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
        {
            QueryCalls++;
            return Task.FromResult(failure is null
                ? Result<IReadOnlyList<WorkshopItem>>.Success(ids.Where(items.ContainsKey).Select(id => items[id]).ToArray())
                : Result<IReadOnlyList<WorkshopItem>>.Failure(failure));
        }
        public Task<Result<WorkshopItem?>> GetItemAsync(WorkshopId publishedFileId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<WorkshopId>>> GetSubscribedItemsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Result<WorkshopLocalState>> GetLocalStateAsync(WorkshopId publishedFileId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Result> RequestDownloadAsync(WorkshopId publishedFileId, bool highPriority, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Result<string?>> GetPersonaNameAsync(ulong steamId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
