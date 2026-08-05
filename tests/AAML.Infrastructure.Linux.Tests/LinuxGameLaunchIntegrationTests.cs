using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Application.Steam;
using AAML.Domain.Games;
using AAML.Domain.Launching;
using AAML.Domain.Mods;
using AAML.Infrastructure.Common.Files;
using AAML.Infrastructure.Linux.Launching;
using FluentAssertions;

namespace AAML.Infrastructure.Linux.Tests;

[TestClass]
public sealed class LinuxGameLaunchIntegrationTests
{
    [TestMethod]
    public async Task ProtonLayout_WritesVariantConfigurationWithWineWorkshopPath()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("Proton filesystem integration requires Linux.");
        var fixture = CreateFixture(GameVariant.XCom2WarOfTheChosen);
        try
        {
            var request = Request(fixture.Game, GameVariant.XCom2WarOfTheChosen);

            var result = await new LinuxGameConfigurationWriter(new AtomicTextWriter()).ApplyAsync(request, TestContext.CancellationToken);

            result.IsSuccess.Should().BeTrue();
            var options = await File.ReadAllTextAsync(result.Value!.WrittenFiles[0], TestContext.CancellationToken);
            var engine = await File.ReadAllTextAsync(result.Value.WrittenFiles[1], TestContext.CancellationToken);
            options.Should().Contain("ActiveMods=AllRegionLinks");
            engine.Should().Contain("ModRootDirs=S:\\workshop\\content\\268500\\");
            engine.Should().Contain("ModRootDirs=Z:\\home\\jake\\Workshop\\");
        }
        finally { Directory.Delete(fixture.Root, true); }
    }

    [TestMethod]
    public async Task Launch_PublishesExactVariantRequestBeforeStartingSteam()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("Proton filesystem integration requires Linux.");
        var fixture = CreateFixture(GameVariant.XCom2);
        try
        {
            var store = new RecordingStore();
            var steam = new RecordingSteamLauncher();

            var result = await new LinuxSteamGameLauncher(store, steam).LaunchAsync(Request(fixture.Game, GameVariant.XCom2), TestContext.CancellationToken);

            result.Value!.ProcessId.Should().Be(42);
            store.Published!.Variant.Should().Be(GameVariant.XCom2);
            store.Published.TargetExecutablePath.Should().EndWith("/Binaries/Win64/XCom2.exe");
            store.Published.AdditionalArguments.Should().Equal("-review", "-noRedScreens");
            steam.AppId.Should().Be(SteamAppId.Xcom2);
        }
        finally { Directory.Delete(fixture.Root, true); }
    }

    public TestContext TestContext { get; set; }

    private static GameLaunchRequest Request(string game, GameVariant variant) => new(variant, game,
        [Path.Combine(Directory.GetParent(game)!.Parent!.FullName, "workshop", "content", "268500"), "/home/jake/Workshop"],
        [new GameLaunchMod(new ModKey(ModSource.SteamWorkshop, "/home/jake/Workshop/630044970"), new PackageId("AllRegionLinks"), 0, false)],
        [new LaunchArgument("-review"), new LaunchArgument("-noRedScreens")]);

    private static (string Root, string Game) CreateFixture(GameVariant variant)
    {
        var root = Path.Combine(Path.GetTempPath(), "aaml-proton-" + Guid.NewGuid().ToString("N"));
        var game = Path.Combine(root, "steamapps", "common", "XCOM 2");
        var variantRoot = variant == GameVariant.XCom2 ? game : Path.Combine(game, "XCom2-WarOfTheChosen");
        Directory.CreateDirectory(Path.Combine(variantRoot, "Binaries", "Win64"));
        File.WriteAllBytes(Path.Combine(variantRoot, "Binaries", "Win64", "XCom2.exe"), []);
        Directory.CreateDirectory(Path.Combine(root, "steamapps", "compatdata", "268500", "pfx", "drive_c", "users", "steamuser"));
        return (root, game);
    }

    private sealed class RecordingStore : ISteamLaunchRequestStore
    {
        public SteamLaunchRequest? Published { get; private set; }
        public Task<Result<SteamLaunchTicket>> PublishAsync(SteamLaunchRequest request, CancellationToken cancellationToken) { Published = request; return Task.FromResult(Result<SteamLaunchTicket>.Success(new SteamLaunchTicket(request.RequestId, request.AppId, request.ExpiresAtUtc))); }
        public Task<Result<ClaimedSteamLaunchRequest?>> TryClaimAsync(SteamAppId invokedAppId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingSteamLauncher : ISteamAppLauncher
    {
        public SteamAppId AppId { get; private set; }
        public Result<int> Start(SteamAppId appId) { AppId = appId; return Result<int>.Success(42); }
    }
}
