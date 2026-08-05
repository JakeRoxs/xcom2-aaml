using AAML.Application.Common;
using AAML.Application.Mods.Workshop;
using AAML.Application.Ports;
using AAML.Domain.Mods;
using FluentAssertions;

namespace AAML.Application.Tests;

[TestClass]
public sealed class WorkshopOperationCoordinatorTests
{
    [TestMethod]
    public async Task Refresh_DeduplicatesWorkshopIdsFansOutStableKeysAndPreservesPartialFailure()
    {
        var first = Mod("C:\\One", 10); var duplicate = Mod("C:\\Duplicate", 10); var failed = Mod("C:\\Failed", 20);
        var service = new ScriptedWorkshopService();
        service.States[10] = new Queue<Result<WorkshopLocalState>>([Success(10, WorkshopItemState.Installed)]);
        service.States[20] = new Queue<Result<WorkshopLocalState>>([Result<WorkshopLocalState>.Failure(new Error("steam.not_running", "Offline", ErrorKind.Unavailable))]);

        var result = await new WorkshopOperationCoordinator(service).RefreshAsync([first, duplicate, failed], null, TestContext.CancellationToken);

        result.Items.Select(item => item.Mod).Should().Equal(first.Key, duplicate.Key, failed.Key);
        result.Items.Take(2).Should().OnlyContain(item => item.State!.Update == UpdateStatus.Current && item.Outcome.IsSuccess);
        result.Items[2].Outcome.Error!.Code.Should().Be("steam.not_running");
        result.IsPartialSuccess.Should().BeTrue();
        service.StateCalls.Should().Equal(10UL, 20UL);
    }

    [TestMethod]
    public async Task Refresh_DownloadingPrecedesNeedsUpdateAndPreservesBytes()
    {
        var mod = Mod("C:\\One", 10);
        var service = new ScriptedWorkshopService();
        service.States[10] = new Queue<Result<WorkshopLocalState>>([Success(10, WorkshopItemState.Installed | WorkshopItemState.NeedsUpdate | WorkshopItemState.Downloading, 25, 100)]);

        var result = await new WorkshopOperationCoordinator(service).RefreshAsync([mod], null, TestContext.CancellationToken);

        result.Items.Single().State.Should().Match<WorkshopModState>(state => state.Update == UpdateStatus.Downloading && state.Download!.Fraction == 0.25);
    }

    [TestMethod]
    public async Task Download_PollsPendingDownloadingThenCurrentAndReportsProgress()
    {
        var mod = Mod("C:\\One", 10);
        var service = new ScriptedWorkshopService();
        service.States[10] = new Queue<Result<WorkshopLocalState>>([
            Success(10, WorkshopItemState.Installed | WorkshopItemState.NeedsUpdate),
            Success(10, WorkshopItemState.Installed | WorkshopItemState.NeedsUpdate | WorkshopItemState.DownloadPending),
            Success(10, WorkshopItemState.Installed | WorkshopItemState.NeedsUpdate | WorkshopItemState.Downloading, 50, 100),
            Success(10, WorkshopItemState.Installed)]);
        var progress = new List<WorkshopOperationProgress>();

        var result = await new WorkshopOperationCoordinator(service).DownloadUpdatesAsync([mod], new WorkshopDownloadOptions(TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2), true), new InlineProgress<WorkshopOperationProgress>(progress.Add), TestContext.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Items.Single().State!.Update.Should().Be(UpdateStatus.Current);
        service.DownloadRequests.Should().Equal((10UL, true));
        progress.Should().Contain(item => item.BytesDownloaded == 50 && item.BytesTotal == 100);
        progress.Last().CompletedItems.Should().Be(1);
        progress.Last().TotalItems.Should().Be(1);
        progress.Where(item => item.BytesTotal == 100).Select(item => item.BytesDownloaded).Should().BeInAscendingOrder();
    }

    [TestMethod]
    public async Task Download_AggregateBytesDoNotRegressWhenOneOfMultipleItemsCompletes()
    {
        var first = Mod("C:\\One", 10); var second = Mod("C:\\Two", 20);
        var service = new ScriptedWorkshopService();
        service.States[10] = new Queue<Result<WorkshopLocalState>>([
            Success(10, WorkshopItemState.Downloading, 50, 100),
            Success(10, WorkshopItemState.Installed)]);
        service.States[20] = new Queue<Result<WorkshopLocalState>>([
            Success(20, WorkshopItemState.Downloading, 10, 100),
            Success(20, WorkshopItemState.Downloading, 20, 100),
            Success(20, WorkshopItemState.Installed)]);
        var progress = new List<WorkshopOperationProgress>();

        var result = await new WorkshopOperationCoordinator(service).DownloadUpdatesAsync([first, second], new WorkshopDownloadOptions(TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2), true), new InlineProgress<WorkshopOperationProgress>(progress.Add), TestContext.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        var aggregate = progress.Where(item => item.Operation == "workshop.download" && item.BytesTotal.HasValue).Select(item => item.BytesDownloaded).ToArray();
        aggregate.Should().BeInAscendingOrder();
        progress.Last().CompletedItems.Should().Be(2);
    }

    [TestMethod]
    public async Task Download_CancellationStopsMonitoringButStatesSteamMayContinue()
    {
        var mod = Mod("C:\\One", 10);
        using var source = new CancellationTokenSource();
        var service = new ScriptedWorkshopService();
        service.States[10] = new Queue<Result<WorkshopLocalState>>([
            Success(10, WorkshopItemState.Installed | WorkshopItemState.NeedsUpdate),
            Success(10, WorkshopItemState.Installed | WorkshopItemState.NeedsUpdate | WorkshopItemState.Downloading)]);
        service.AfterStateCall = count => { if (count == 2) source.Cancel(); };

        var result = await new WorkshopOperationCoordinator(service).DownloadUpdatesAsync([mod], new WorkshopDownloadOptions(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1), true), null, source.Token);

        result.ObservationCancelled.Should().BeTrue();
        result.Items.Single().Outcome.Error!.Code.Should().Be("workshop.monitoring_cancelled");
        result.Items.Single().Outcome.Error!.Metadata!["steamDownloadMayContinue"].Should().Be(bool.TrueString);
        service.DownloadRequests.Should().ContainSingle();
    }

    public TestContext TestContext { get; set; }

    private static ModInstallation Mod(string path, ulong id) => new(new ModKey(ModSource.SteamWorkshop, path), new PackageId("Package" + id), "Mod " + id, new WorkshopId(id), true, DescriptorState.Enabled, null);
    private static Result<WorkshopLocalState> Success(ulong id, WorkshopItemState state, ulong downloaded = 0, ulong total = 0) => Result<WorkshopLocalState>.Success(new WorkshopLocalState(new WorkshopId(id), state, null, total > 0 ? new WorkshopDownloadInfo(downloaded, total, (double)downloaded / total) : null));

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
    private sealed class ScriptedWorkshopService : IWorkshopService
    {
        public Dictionary<ulong, Queue<Result<WorkshopLocalState>>> States { get; } = [];
        public List<ulong> StateCalls { get; } = [];
        public List<(ulong Id, bool Priority)> DownloadRequests { get; } = [];
        public Action<int>? AfterStateCall { get; set; }
        public Task<Result<WorkshopLocalState>> GetLocalStateAsync(WorkshopId id, CancellationToken cancellationToken)
        {
            StateCalls.Add(id.Value);
            var result = States[id.Value].Count > 1 ? States[id.Value].Dequeue() : States[id.Value].Peek();
            AfterStateCall?.Invoke(StateCalls.Count);
            return Task.FromResult(result);
        }
        public Task<Result> RequestDownloadAsync(WorkshopId id, bool highPriority, CancellationToken cancellationToken) { DownloadRequests.Add((id.Value, highPriority)); return Task.FromResult(Result.Success()); }
        public Task<Result<WorkshopItem?>> GetItemAsync(WorkshopId id, CancellationToken token) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<WorkshopItem>>> GetItemsAsync(IReadOnlyList<WorkshopId> ids, IProgress<OperationProgress>? progress, CancellationToken token) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<WorkshopId>>> GetSubscribedItemsAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<Result<string?>> GetPersonaNameAsync(ulong steamId, CancellationToken token) => throw new NotSupportedException();
    }
}
