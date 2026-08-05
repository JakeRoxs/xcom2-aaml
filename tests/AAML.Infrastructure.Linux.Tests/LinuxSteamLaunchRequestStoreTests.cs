using System.Text.Json;
using AAML.Application.Common;
using AAML.Application.Steam;
using AAML.Domain.Games;
using AAML.Infrastructure.Linux.Steam;
using FluentAssertions;

namespace AAML.Infrastructure.Linux.Tests;

[TestClass]
public sealed class LinuxSteamLaunchRequestStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task PublishClaimAndReplay_AreOneShot()
    {
        var root = CreateRoot();
        try
        {
            var store = new LinuxSteamLaunchRequestStore(root, new FixedTimeProvider(Now));
            var request = Request();

            var published = await store.PublishAsync(request, TestContext.CancellationToken);
            var claimed = await store.TryClaimAsync(SteamAppId.Xcom2, Now, TestContext.CancellationToken);
            var replay = await store.TryClaimAsync(SteamAppId.Xcom2, Now, TestContext.CancellationToken);

            published.Value!.RequestId.Should().Be(request.RequestId);
            claimed.Value!.Request.Should().BeEquivalentTo(request);
            replay.IsSuccess.Should().BeTrue();
            replay.Value.Should().BeNull();
            Directory.EnumerateFiles(Path.Combine(root, "steam-launch")).Should().BeEmpty();
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task PendingSlot_RefusesOverwriteAndWrongAppDoesNotConsume()
    {
        var root = CreateRoot();
        try
        {
            var store = new LinuxSteamLaunchRequestStore(root, new FixedTimeProvider(Now));
            await store.PublishAsync(Request(), TestContext.CancellationToken);

            var duplicate = await store.PublishAsync(Request(), TestContext.CancellationToken);
            var wrongApp = await store.TryClaimAsync(SteamAppId.ChimeraSquad, Now, TestContext.CancellationToken);
            var correctApp = await store.TryClaimAsync(SteamAppId.Xcom2, Now, TestContext.CancellationToken);

            duplicate.Error!.Code.Should().Be("steam.launch.request_pending");
            wrongApp.IsSuccess.Should().BeTrue();
            wrongApp.Value.Should().BeNull();
            correctApp.Value.Should().NotBeNull();
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task StaleAndMalformedRequests_AreConsumedAndFailClosed()
    {
        var root = CreateRoot();
        try
        {
            var store = new LinuxSteamLaunchRequestStore(root, new FixedTimeProvider(Now));
            await store.PublishAsync(Request(), TestContext.CancellationToken);
            var stale = await store.TryClaimAsync(SteamAppId.Xcom2, Now.AddMinutes(1), TestContext.CancellationToken);
            stale.Error!.Code.Should().Be("steam.launch.request_expired");

            var launchDirectory = Path.Combine(root, "steam-launch");
            await File.WriteAllTextAsync(Path.Combine(launchDirectory, "request-268500.json"), "{ malformed", TestContext.CancellationToken);
            var malformed = await store.TryClaimAsync(SteamAppId.Xcom2, Now, TestContext.CancellationToken);

            malformed.Error!.Code.Should().Be("steam.launch.request_malformed");
            Directory.EnumerateFiles(launchDirectory).Should().BeEmpty();
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task LinuxFiles_ArePrivateWhenRunningOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Unix mode validation requires Linux.");
            return;
        }
        var root = CreateRoot();
        try
        {
            var store = new LinuxSteamLaunchRequestStore(root, new FixedTimeProvider(Now));
            await store.PublishAsync(Request(), TestContext.CancellationToken);
            var directory = Path.Combine(root, "steam-launch");
            var file = Path.Combine(directory, "request-268500.json");

            File.GetUnixFileMode(directory).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.GetUnixFileMode(file).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally { Directory.Delete(root, true); }
    }

    public TestContext TestContext { get; set; }

    private static SteamLaunchRequest Request() => new(
        SteamLaunchRequestPolicy.CurrentProtocolVersion, Guid.NewGuid(), SteamAppId.Xcom2, GameVariant.XCom2,
        "/games/XCOM 2", "/games/XCOM 2/Binaries/Win64/XCom2.exe",
        ["AllRegionLinks"], ["/games/XCOM 2/Workshop"], ["-Name=Mixed Case", "&;|$() Ω"], Now, Now.AddSeconds(30));

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML Steam Launch Ω", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
