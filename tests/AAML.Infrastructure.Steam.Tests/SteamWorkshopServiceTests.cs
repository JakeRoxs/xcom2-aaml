using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Domain.Mods;
using AAML.Infrastructure.Steam.Internal;
using FluentAssertions;

namespace AAML.Infrastructure.Steam.Tests;

[TestClass]
public sealed class SteamWorkshopServiceTests
{
    [TestMethod]
    public async Task Query_MapsChildrenPreservesRequestedOrderAndAlwaysReleasesHandle()
    {
        var api = new FakeClientApi();
        var ugc = new FakeUgcApi
        {
            Items =
            [
                new SteamWorkshopSnapshot(2, "Second", [4, 4, 0], "Long description", 76561198000000000, "Strategy, WotC", false, 100, 200, 300, "https://cdn.example.test/preview.png"),
                new SteamWorkshopSnapshot(1, "First", [3])
            ]
        };
        var service = CreateService(api, ugc, new FakeCallbacks { ResultCount = 2 });

        var result = await service.GetItemsAsync([new WorkshopId(1), new WorkshopId(2)], null, TestContext.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Select(item => item.PublishedFileId.Value).Should().Equal(1UL, 2UL);
        result.Value![1].ChildIds.Select(id => id.Value).Should().Equal(4UL);
        result.Value[1].Description.Should().Be("Long description");
        result.Value[1].OwnerSteamId.Should().Be(76561198000000000);
        result.Value[1].Tags.Should().Equal("Strategy", "WotC");
        result.Value[1].CreatedAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(100));
        result.Value[1].PreviewUrl.Should().Be("https://cdn.example.test/preview.png");
        ugc.ReleaseCount.Should().Be(1);
        await DisposeLifetimeAsync(api);
        api.ShutdownCount.Should().Be(1);
    }

    [TestMethod]
    [DataRow("http://cdn.example.test/preview.png")]
    [DataRow("/preview.png")]
    [DataRow("not a URL")]
    public async Task Query_RejectsNonAbsoluteHttpsPreviewUrls(string previewUrl)
    {
        var api = new FakeClientApi();
        var ugc = new FakeUgcApi { Items = [new SteamWorkshopSnapshot(1, "One", [], PreviewUrl: previewUrl)] };
        var service = CreateService(api, ugc, new FakeCallbacks { ResultCount = 1 });

        var result = await service.GetItemAsync(new WorkshopId(1), TestContext.CancellationToken);

        result.Value!.PreviewUrl.Should().BeNull();
        await DisposeLifetimeAsync(api);
    }

    [TestMethod]
    public async Task QueryFailure_StillReleasesHandle()
    {
        var api = new FakeClientApi();
        var ugc = new FakeUgcApi { ConfigureSuccess = false };
        var service = CreateService(api, ugc, new FakeCallbacks());

        var result = await service.GetItemAsync(new WorkshopId(1), TestContext.CancellationToken);

        result.Error!.Code.Should().Be("steam.query_configuration_failed");
        ugc.ReleaseCount.Should().Be(1);
        await DisposeLifetimeAsync(api);
    }

    [TestMethod]
    public async Task Query_SkipsMalformedRowsWithoutDiscardingValidMetadata()
    {
        var api = new FakeClientApi();
        var ugc = new FakeUgcApi
        {
            Items = [new SteamWorkshopSnapshot(1, "Invalid", []), new SteamWorkshopSnapshot(2, "Valid", [3])],
            InvalidIndices = [0]
        };
        var service = CreateService(api, ugc, new FakeCallbacks { ResultCount = 2 });

        var result = await service.GetItemsAsync([new WorkshopId(1), new WorkshopId(2)], null, TestContext.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle(item => item.PublishedFileId == new WorkshopId(2));
        ugc.ReleaseCount.Should().Be(1);
        await DisposeLifetimeAsync(api);
    }

    [TestMethod]
    public async Task CleanupFailure_OverridesSuccessfulQuery()
    {
        var api = new FakeClientApi();
        var ugc = new FakeUgcApi { ReleaseSuccess = false, Items = [new SteamWorkshopSnapshot(1, "One", [])] };
        var service = CreateService(api, ugc, new FakeCallbacks());

        var result = await service.GetItemAsync(new WorkshopId(1), TestContext.CancellationToken);

        result.Error!.Code.Should().Be("steam.query_cleanup_failed");
        await DisposeLifetimeAsync(api);
    }

    [TestMethod]
    public async Task InitializationFailure_IsTruthfulAndStartsNoPump()
    {
        var api = new FakeClientApi { Initialization = new SteamInitialization(false, "steam.not_running", "Steam is not running.") };
        var service = CreateService(api, new FakeUgcApi(), new FakeCallbacks());

        var result = await service.GetItemAsync(new WorkshopId(1), TestContext.CancellationToken);

        result.Error!.Code.Should().Be("steam.not_running");
        api.RunCallbackCount.Should().Be(0);
        await DisposeLifetimeAsync(api);
        api.ShutdownCount.Should().Be(0);
    }

    [TestMethod]
    public async Task FiftyOneDistinctIds_AreSplitIntoTwoQueries()
    {
        var api = new FakeClientApi();
        var ugc = new FakeUgcApi();
        var service = CreateService(api, ugc, new FakeCallbacks());
        var ids = Enumerable.Range(1, 51).Select(value => new WorkshopId((ulong)value)).ToArray();

        var result = await service.GetItemsAsync(ids, null, TestContext.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        ugc.CreatedBatches.Select(batch => batch.Count).Should().Equal(50, 1);
        ugc.ReleaseCount.Should().Be(2);
        await DisposeLifetimeAsync(api);
    }

    [TestMethod]
    public async Task CallerCancellation_IsDistinctAndReleasesQuery()
    {
        using var source = new CancellationTokenSource();
        var callbacks = new FakeCallbacks
        {
            Wait = async token =>
            {
                await source.CancelAsync();
                await Task.Delay(50, token);
                if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                return new SteamQueryCompletion(new SteamQueryHandle(1), false, false, 0, "Cancelled");
            }
        };
        var api = new FakeClientApi();
        var ugc = new FakeUgcApi();
        var service = CreateService(api, ugc, callbacks);

        var result = await service.GetItemAsync(new WorkshopId(1), source.Token);

        result.Error!.Kind.Should().Be(ErrorKind.Cancelled);
        ugc.ReleaseCount.Should().Be(1);
        await DisposeLifetimeAsync(api);
    }

    [TestMethod]
    public async Task LocalOperations_MapSubscriptionsInstallDownloadAndPersona()
    {
        var api = new FakeClientApi();
        var ugc = new FakeUgcApi
        {
            Subscriptions = [2, 1, 2, 0],
            State = (uint)(WorkshopItemState.Subscribed | WorkshopItemState.Installed),
            Install = new SteamInstallSnapshot(1234, "C:\\Steam\\Workshop\\1", 1_700_000_000),
            Download = new SteamDownloadSnapshot(1, 2)
        };
        var callbacks = new FakeCallbacks { PersonaName = "Fixture User" };
        var service = CreateService(api, ugc, callbacks);

        var subscriptions = await service.GetSubscribedItemsAsync(TestContext.CancellationToken);
        var state = await service.GetLocalStateAsync(new WorkshopId(1), TestContext.CancellationToken);
        var download = await service.RequestDownloadAsync(new WorkshopId(1), true, TestContext.CancellationToken);
        var persona = await service.GetPersonaNameAsync(42, TestContext.CancellationToken);

        subscriptions.Value!.Select(id => id.Value).Should().Equal(2UL, 1UL);
        state.Value!.State.Should().HaveFlag(WorkshopItemState.Installed);
        state.Value.Install!.InstalledAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        state.Value.Download!.Fraction.Should().Be(0.5);
        download.IsSuccess.Should().BeTrue();
        ugc.LastDownload.Should().Be((1UL, true));
        persona.Value.Should().Be("Fixture User");
        await DisposeLifetimeAsync(api);
    }

    [TestMethod]
    public async Task Download_RejectionExceptionAndCancellationRemainStructured()
    {
        var rejectedApi = new FakeClientApi();
        var rejectedUgc = new FakeUgcApi { DownloadAccepted = false };
        var rejected = CreateService(rejectedApi, rejectedUgc, new FakeCallbacks());
        (await rejected.RequestDownloadAsync(new WorkshopId(1), true, TestContext.CancellationToken)).Error!.Code.Should().Be("steam.download_rejected");
        await DisposeLifetimeAsync(rejectedApi);

        var throwingApi = new FakeClientApi();
        var throwingUgc = new FakeUgcApi { DownloadException = new InvalidOperationException("fixture") };
        var throwing = CreateService(throwingApi, throwingUgc, new FakeCallbacks());
        (await throwing.RequestDownloadAsync(new WorkshopId(1), true, TestContext.CancellationToken)).Error!.Code.Should().Be("steam.download_failed");
        await DisposeLifetimeAsync(throwingApi);

        var cancelledApi = new FakeClientApi();
        var cancelledUgc = new FakeUgcApi();
        var cancelled = CreateService(cancelledApi, cancelledUgc, new FakeCallbacks());
        using var source = new CancellationTokenSource();
        await source.CancelAsync();
        (await cancelled.RequestDownloadAsync(new WorkshopId(1), true, source.Token)).Error!.Kind.Should().Be(ErrorKind.Cancelled);
        cancelledUgc.LastDownload.Should().BeNull();
        await DisposeLifetimeAsync(cancelledApi);
    }

    [TestMethod]
    public async Task SubscriptionMutations_WaitForMatchingSteamCompletion()
    {
        var api = new FakeClientApi(); var ugc = new FakeUgcApi(); var callbacks = new FakeCallbacks(); var service = CreateService(api, ugc, callbacks);
        callbacks.Mutation = (call, _, _) => Task.FromResult(new SteamMutationCompletion(call.Value == 11 ? 42UL : 43UL, false, true, "OK"));

        (await service.SubscribeAsync(new WorkshopId(42), TestContext.CancellationToken)).IsSuccess.Should().BeTrue();
        (await service.UnsubscribeAsync(new WorkshopId(43), TestContext.CancellationToken)).IsSuccess.Should().BeTrue();
        ugc.SubscriptionRequests.Should().Equal((42UL, true), (43UL, false));
        await DisposeLifetimeAsync(api);
    }

    public TestContext TestContext { get; set; }

    private static async Task DisposeLifetimeAsync(FakeClientApi api)
    {
        var lifetime = api.Lifetime ?? throw new InvalidOperationException("Steam client lifetime should be initialized.");
        await lifetime.DisposeAsync();
    }

    private static SteamWorkshopService CreateService(FakeClientApi api, FakeUgcApi ugc, FakeCallbacks callbacks)
    {
        var options = new SteamOptions(TimeSpan.FromHours(1), TimeSpan.FromSeconds(1));
        var lifetime = new SteamClientLifetime(api, options);
        api.Lifetime = lifetime;
        return new SteamWorkshopService(lifetime, ugc, callbacks, options);
    }

    private sealed class FakeClientApi : ISteamClientApi
    {
        public SteamInitialization Initialization { get; set; } = new(true, string.Empty, string.Empty);
        public int RunCallbackCount { get; private set; }
        public int ShutdownCount { get; private set; }
        public SteamClientLifetime? Lifetime { get; set; }
        public SteamInitialization Initialize() => Initialization;
        public void RunCallbacks() => RunCallbackCount++;
        public void Shutdown() => ShutdownCount++;
    }

    private sealed class FakeUgcApi : ISteamUgcApi
    {
        public bool ConfigureSuccess { get; set; } = true;
        public bool ReleaseSuccess { get; set; } = true;
        public HashSet<uint> InvalidIndices { get; set; } = [];
        public IReadOnlyList<SteamWorkshopSnapshot> Items { get; set; } = [];
        public int ReleaseCount { get; private set; }
        public List<IReadOnlyList<ulong>> CreatedBatches { get; } = [];
        public IReadOnlyList<ulong> Subscriptions { get; set; } = [];
        public uint State { get; set; }
        public SteamInstallSnapshot? Install { get; set; }
        public SteamDownloadSnapshot? Download { get; set; }
        public (ulong Id, bool HighPriority)? LastDownload { get; private set; }
        public bool DownloadAccepted { get; set; } = true;
        public Exception? DownloadException { get; set; }
        public List<(ulong Id, bool Subscribe)> SubscriptionRequests { get; } = [];
        public SteamQueryHandle CreateDetailsQuery(IReadOnlyList<ulong> ids) { CreatedBatches.Add(ids); return new((ulong)CreatedBatches.Count); }
        public bool SetReturnChildren(SteamQueryHandle handle, bool enabled) => ConfigureSuccess;
        public bool SetReturnLongDescription(SteamQueryHandle handle, bool enabled) => ConfigureSuccess;
        public SteamAsyncCall SendQuery(SteamQueryHandle handle) => new(handle.Value);
        public bool TryGetQueryItem(SteamQueryHandle handle, uint index, out SteamWorkshopSnapshot item)
        {
            if (InvalidIndices.Contains(index))
            {
                item = new SteamWorkshopSnapshot(0, string.Empty, []);
                return false;
            }

            if (index < Items.Count)
            {
                item = Items[(int)index];
                return true;
            }

            item = new SteamWorkshopSnapshot(0, string.Empty, []);
            return false;
        }
        public bool ReleaseQuery(SteamQueryHandle handle) { ReleaseCount++; return ReleaseSuccess; }
        public IReadOnlyList<ulong> GetSubscribedItems() => Subscriptions;
        public uint GetItemState(ulong id) => State;
        public bool TryGetInstallInfo(ulong id, out SteamInstallSnapshot install)
        {
            if (Install is null)
            {
                install = new SteamInstallSnapshot(0, string.Empty, 0);
                return false;
            }

            install = Install;
            return true;
        }
        public bool TryGetDownloadInfo(ulong id, out SteamDownloadSnapshot download)
        {
            if (Download is null)
            {
                download = new SteamDownloadSnapshot(0, 0);
                return false;
            }

            download = Download;
            return true;
        }
        public bool DownloadItem(ulong id, bool highPriority)
        {
            if (DownloadException is not null)
            {
                throw DownloadException;
            }

            LastDownload = (id, highPriority);
            return DownloadAccepted;
        }
        public SteamAsyncCall SubscribeItem(ulong id) { SubscriptionRequests.Add((id, true)); return new(11); }
        public SteamAsyncCall UnsubscribeItem(ulong id) { SubscriptionRequests.Add((id, false)); return new(12); }
    }

    private sealed class FakeCallbacks : ISteamCallbacks
    {
        public Func<CancellationToken, Task<SteamQueryCompletion>>? Wait { get; set; }
        public uint ResultCount { get; set; }
        public string? PersonaName { get; set; }
        public Func<SteamAsyncCall, SteamSubscriptionMutation, CancellationToken, Task<SteamMutationCompletion>>? Mutation { get; set; }
        public Task<SteamQueryCompletion> WaitForQueryAsync(SteamAsyncCall call, CancellationToken cancellationToken) =>
            Wait?.Invoke(cancellationToken) ?? Task.FromResult(new SteamQueryCompletion(new SteamQueryHandle(call.Value), false, true, ResultCount, string.Empty));
        public Task<string?> GetPersonaNameAsync(ulong steamId, CancellationToken cancellationToken) => Task.FromResult(PersonaName);
        public Task<SteamMutationCompletion> WaitForSubscriptionMutationAsync(SteamAsyncCall call, SteamSubscriptionMutation mutation, CancellationToken cancellationToken) => Mutation?.Invoke(call, mutation, cancellationToken) ?? Task.FromResult(new SteamMutationCompletion(0, false, false, "Unsupported"));
    }
}
