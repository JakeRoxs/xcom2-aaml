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

            var result = await new LinuxGameLauncher(store, steam).LaunchAsync(Request(fixture.Game, GameVariant.XCom2), TestContext.CancellationToken);

            result.Value!.ProcessId.Should().Be(42);
            store.Published!.Variant.Should().Be(GameVariant.XCom2);
            store.Published.TargetExecutablePath.Should().EndWith("/Binaries/Win64/XCom2.exe");
            store.Published.AdditionalArguments.Should().Equal("-review", "-noRedScreens");
            steam.AppId.Should().Be(SteamAppId.Xcom2);
        }
        finally { Directory.Delete(fixture.Root, true); }
    }

    [TestMethod]
    public async Task MixedCaseKnownArtifacts_ResolveForConfigurationAndLaunch()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("Proton filesystem integration requires Linux.");
        var fixture = CreateMixedCaseFixture();
        try
        {
            var request = Request(fixture.Game, GameVariant.XCom2WarOfTheChosen);
            var configured = await new LinuxGameConfigurationWriter(new AtomicTextWriter()).ApplyAsync(request, TestContext.CancellationToken);
            var store = new RecordingStore();
            var launched = await new LinuxGameLauncher(store, new RecordingSteamLauncher()).LaunchAsync(request, TestContext.CancellationToken);
            var layout = LinuxGameRuntimeLayout.Resolve(fixture.Game, GameVariant.XCom2WarOfTheChosen, GameRuntime.Proton);

            configured.IsSuccess.Should().BeTrue(configured.Error?.Message);
            configured.Value!.WrittenFiles.Should().OnlyContain(path => path.Contains("documents/my games/xcom2 war of the chosen/xcomgame/config", StringComparison.Ordinal));
            launched.IsSuccess.Should().BeTrue(launched.Error?.Message);
            store.Published!.TargetExecutablePath.Should().EndWith("/xcom2-warOFthechosen/binaries/WIN64/XCOM2.EXE");
            layout.Value!.CaseFallbacks.Should().HaveCountGreaterThan(6);
        }
        finally { Directory.Delete(fixture.Root, true); }
    }

    [TestMethod]
    public async Task NativeLayout_ResolvesXCom2VanillaExecutable()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("Native layout integration requires Linux.");
        var fixture = CreateNativeFixture(GameVariant.XCom2);
        try
        {
            var layout = LinuxGameRuntimeLayout.Resolve(fixture.Game, GameVariant.XCom2, GameRuntime.Auto);
            layout.IsSuccess.Should().BeTrue(layout.Error?.Message);
            layout.Value!.UsesProtonPrefix.Should().BeFalse();
            layout.Value.TargetExecutablePath.Should().EndWith("/XCOM2/Binaries/Linux/XCOM2");
            layout.Value.Runtime.Should().Be(GameRuntime.Native);
        }
        finally { Directory.Delete(fixture.Root, true); }
    }

    [TestMethod]
    public async Task NativeLayout_ResolvesWotCExecutable()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("Native layout integration requires Linux.");
        var fixture = CreateNativeFixture(GameVariant.XCom2WarOfTheChosen);
        try
        {
            var layout = LinuxGameRuntimeLayout.Resolve(fixture.Game, GameVariant.XCom2WarOfTheChosen, GameRuntime.Auto);
            layout.IsSuccess.Should().BeTrue(layout.Error?.Message);
            layout.Value!.UsesProtonPrefix.Should().BeFalse();
            layout.Value.TargetExecutablePath.Should().EndWith("/XCom2-WarOfTheChosen/Binaries/Linux/XCOM2WOTC");
            layout.Value.Runtime.Should().Be(GameRuntime.Native);
        }
        finally { Directory.Delete(fixture.Root, true); }
    }

    [TestMethod]
    public async Task MixedLayout_AutoPrefersNativeWhenBothAvailable()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("Mixed layout integration requires Linux.");
        var root = Path.Combine(Path.GetTempPath(), "aaml-mixed-" + Guid.NewGuid().ToString("N"));
        try
        {
            var game = Path.Combine(root, "steamapps", "common", "XCOM 2");
            Directory.CreateDirectory(Path.Combine(game, "XCOM2", "Binaries", "Linux"));
            Directory.CreateDirectory(Path.Combine(game, "Binaries", "Win64"));
            Directory.CreateDirectory(Path.Combine(root, "steamapps", "compatdata", "268500", "pfx", "drive_c", "users", "steamuser"));
            File.WriteAllText(Path.Combine(root, "steamapps", "appmanifest_268500.acf"), "\"AppState\" { \"appid\" \"268500\" \"installdir\" \"XCOM 2\" }");
            File.WriteAllBytes(Path.Combine(game, "XCOM2", "Binaries", "Linux", "XCOM2"), []);
            File.WriteAllBytes(Path.Combine(game, "Binaries", "Win64", "XCom2.exe"), []);

            var layout = LinuxGameRuntimeLayout.Resolve(game, GameVariant.XCom2, GameRuntime.Auto);
            layout.IsSuccess.Should().BeTrue(layout.Error?.Message);
            layout.Value!.UsesProtonPrefix.Should().BeFalse();
            layout.Value.Runtime.Should().Be(GameRuntime.Native);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow(false, false, "steam", "-applaunch|268500")]
    [DataRow(true, false, "flatpak", "run|com.valvesoftware.Steam|-applaunch|268500")]
    [DataRow(false, true, "flatpak-spawn", "--host|steam|-applaunch|268500")]
    [DataRow(true, true, "flatpak-spawn", "--host|run|com.valvesoftware.Steam|-applaunch|268500")]
    public void SteamLauncher_SelectsHostCommandForLauncherAndSteamPackaging(bool flatpakSteam, bool launcherIsFlatpak, string executable, string arguments)
    {
        var start = SteamAppLauncher.CreateStartInfo(SteamAppId.Xcom2, flatpakSteam, launcherIsFlatpak);

        start.FileName.Should().Be(executable);
        start.ArgumentList.Should().Equal(arguments.Split('|'));
        start.UseShellExecute.Should().BeFalse();
    }

    [TestMethod]
    public void NativeLauncher_FromFlatpak_UsesHostSteamRuntimeAndPreservesArguments()
    {
        var start = LinuxGameLauncher.CreateNativeStartInfo(
            "/home/test/.steam/root/ubuntu12_32/steam-runtime/run.sh",
            "/home/test/.steam/root/steamapps/common/XCOM 2/XCOM2WotC/bin/XCOM2WotC",
            "/home/test/.steam/root/steamapps/common/XCOM 2",
            [new LaunchArgument("-review"), new LaunchArgument("-noRedScreens")],
            268500,
            launcherIsFlatpak: true);

        start.FileName.Should().Be("flatpak-spawn");
        var args = start.ArgumentList.ToList();
        args[0].Should().Be("--host");
        args[1].Should().Be("--env=SteamAppId=268500");
        args[2].Should().Be("--env=SteamGameId=268500");
        args[3].Replace('\\', '/').Should().Be("--env=LD_LIBRARY_PATH=/home/test/.steam/root/steamapps/common/XCOM 2/XCOM2WotC/lib:/home/test/.steam/root/steamapps/common/XCOM 2/lib/x86_64");
        args[4].Replace('\\', '/').Should().EndWith("XCOM2WotC/bin");
        args[5].Should().Be("/home/test/.steam/root/ubuntu12_32/steam-runtime/run.sh");
        args[6].Should().Be("--");
        args[7].Should().Be("/home/test/.steam/root/steamapps/common/XCOM 2/XCOM2WotC/bin/XCOM2WotC");
        args[8].Should().Be("-review");
        args[9].Should().Be("-noRedScreens");
        start.UseShellExecute.Should().BeFalse();
    }

    [TestMethod]
    public void NativeLauncher_ResolvesNestedWotCFeralScript()
    {
        var root = Path.Combine(Path.GetTempPath(), "aaml-feral-script-" + Guid.NewGuid().ToString("N"));
        var script = Path.Combine(root, "XCOM2WotC", "XCOM2WotC.sh");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(script)!);
            File.WriteAllText(script, "#!/bin/bash");

            LinuxGameLauncher.ResolveFeralLauncherScript(root, GameVariant.XCom2WarOfTheChosen)
                .Should().Be(script);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void NativeLauncher_MapsFlatpakFeralScriptToHostSteamRoot()
    {
        var originalDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", "/home/test/.var/app/io.github.jakeroxs.xcom2_aaml/data");

            LinuxGameLauncher.MapFlatpakSteamPathToHost(
                    "/home/test/.var/app/io.github.jakeroxs.xcom2_aaml/data/Steam/steamapps/common/XCOM 2/XCOM2WotC/XCOM2WotC.sh",
                    "/home/test/.steam/root")
                .Should().Be(Path.GetFullPath(Path.Combine(
                    "/home/test/.steam/root", "steamapps", "common", "XCOM 2", "XCOM2WotC", "XCOM2WotC.sh")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", originalDataHome);
        }
    }

    [TestMethod]
    public void NativeVanillaLauncher_ForcesXwaylandForLegacyFeralUi()
    {
        var start = LinuxGameLauncher.CreateNativeStartInfo(
            "/steam/scout-on-soldier-entry-point-v2",
            "/steam/steamapps/common/XCOM 2/XCOM2.sh",
            "/steam/steamapps/common/XCOM 2",
            [],
            268500,
            launcherIsFlatpak: true,
            forceX11: true);

        start.ArgumentList.Should().Contain("--env=SDL_VIDEODRIVER=x11");
    }

    [TestMethod]
    public void FeralPreferences_EnableConfiguredNativeMods()
    {
        const string preferences = """
            <?xml version="1.0" encoding="UTF-8"?>
            <registry><key name="Setup"><value name="DisableAllMods" type="integer">1</value><value name="Keep" type="integer">7</value></key></registry>
            """;

        var updated = LinuxGameConfigurationWriter.SetFeralDisableAllMods(preferences, disable: false);

        updated.Should().Contain("<value name=\"DisableAllMods\" type=\"integer\">0</value>");
        updated.Should().Contain("<value name=\"Keep\" type=\"integer\">7</value>");
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
        Directory.CreateDirectory(Path.Combine(root, "steamapps"));
        File.WriteAllText(Path.Combine(root, "steamapps", "appmanifest_268500.acf"), "\"AppState\" { \"appid\" \"268500\" \"installdir\" \"XCOM 2\" }");
        File.WriteAllBytes(Path.Combine(variantRoot, "Binaries", "Win64", "XCom2.exe"), []);
        Directory.CreateDirectory(Path.Combine(root, "steamapps", "compatdata", "268500", "pfx", "drive_c", "users", "steamuser"));
        return (root, game);
    }

    private static (string Root, string Game) CreateNativeFixture(GameVariant variant)
    {
        var root = Path.Combine(Path.GetTempPath(), "aaml-native-" + Guid.NewGuid().ToString("N"));
        var game = Path.Combine(root, "steamapps", "common", "XCOM 2");
        Directory.CreateDirectory(Path.Combine(root, "steamapps"));
        File.WriteAllText(Path.Combine(root, "steamapps", "appmanifest_268500.acf"), "\"AppState\" { \"appid\" \"268500\" \"installdir\" \"XCOM 2\" }");
        if (variant == GameVariant.XCom2)
        {
            Directory.CreateDirectory(Path.Combine(game, "XCOM2", "Binaries", "Linux"));
            File.WriteAllBytes(Path.Combine(game, "XCOM2", "Binaries", "Linux", "XCOM2"), []);
        }
        else
        {
            Directory.CreateDirectory(Path.Combine(game, "XCom2-WarOfTheChosen", "Binaries", "Linux"));
            File.WriteAllBytes(Path.Combine(game, "XCom2-WarOfTheChosen", "Binaries", "Linux", "XCOM2WOTC"), []);
        }
        return (root, game);
    }

    private static (string Root, string Game) CreateMixedCaseFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "aaml-proton-case-" + Guid.NewGuid().ToString("N"));
        var steamApps = Path.Combine(root, "steamapps");
        var game = Path.Combine(steamApps, "common", "xcom 2");
        var target = Path.Combine(game, "xcom2-warOFthechosen", "binaries", "WIN64", "XCOM2.EXE");
        var userRoot = Path.Combine(steamApps, "CompatData", "268500", "PFX", "Drive_C", "Users", "SteamUser");
        var config = Path.Combine(userRoot, "documents", "my games", "xcom2 war of the chosen", "xcomgame", "config");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(steamApps, "appmanifest_268500.acf"), "\"AppState\" { \"appid\" \"268500\" \"installdir\" \"XCOM 2\" }");
        File.WriteAllBytes(target, []);
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
        public Result<int> Start(SteamAppId appId, bool flatpakSteam) { AppId = appId; return Result<int>.Success(42); }
    }
}
