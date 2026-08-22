using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Application.Steam;
using AAML.Domain.Games;
using AAML.Infrastructure.Linux.Steam;
using FluentAssertions;

namespace AAML.Infrastructure.Linux.Tests;

[TestClass]
public sealed class LinuxEnvironmentDiagnosticServiceTests
{
    [TestMethod]
    public async Task Inspect_ReportsProductionLayoutsFallbacksAndLeavesFilesUnchanged()
    {
        RequireLinux();
        var fixture = CreateFixture();
        try
        {
            var configFiles = Directory.EnumerateFiles(fixture.Config, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
            var discovery = new RecordingDiscovery(Discovery(fixture));
            var service = new LinuxEnvironmentDiagnosticService(discovery);

            var result = await service.InspectAsync(new([GameVariant.XCom2, GameVariant.XCom2WarOfTheChosen], CandidateSteamRoots: [fixture.Root]), TestContext.CancellationToken);

            result.IsSuccess.Should().BeTrue(result.Error?.Message);
            result.Value!.Success.Should().BeTrue();
            result.Value.SelectedInstallation.Should().Be(fixture.Game);
            result.Value.WorkshopRoots.Should().Equal(fixture.Workshop);
            result.Value.ProtonPrefixes.Should().Equal(fixture.Prefix);
            result.Value.Variants.Should().HaveCount(2).And.OnlyContain(variant => variant.Success);
            result.Value.Variants.SelectMany(variant => variant.CaseFallbacks).Should().Contain(fallback => fallback.ActualPath.Contains("documents/my games", StringComparison.Ordinal));
            discovery.LastRequest!.CandidateSteamRoots.Should().Equal(fixture.Root);
            foreach (var (path, contents) in configFiles) File.ReadAllBytes(path).Should().Equal(contents, "diagnostics must remain read-only");
        }
        finally { Directory.Delete(fixture.Root, true); }
    }

    [TestMethod]
    public async Task Inspect_FailsDeterministicallyForMissingAndAmbiguousInstallations()
    {
        RequireLinux();
        var empty = Discovery(null);
        var ambiguous = empty with
        {
            Applications =
            [
                new(SteamAppId.Xcom2, "/steam-a", "/steam-a/manifest", "XCOM 2", "/games/a", null, null, true, true),
                new(SteamAppId.Xcom2, "/steam-b", "/steam-b/manifest", "XCOM 2", "/games/b", null, null, true, true)
            ]
        };

        var missing = await new LinuxEnvironmentDiagnosticService(new RecordingDiscovery(empty))
            .InspectAsync(new([GameVariant.XCom2]), TestContext.CancellationToken);
        var multiple = await new LinuxEnvironmentDiagnosticService(new RecordingDiscovery(ambiguous))
            .InspectAsync(new([GameVariant.XCom2]), TestContext.CancellationToken);

        missing.Error!.Code.Should().Be("linux_environment.installation_missing");
        multiple.Error!.Code.Should().Be("linux_environment.installation_ambiguous");
    }

    [TestMethod]
    public async Task Inspect_ObservesCancellationAfterDiscoveryBeforeVariantLayout()
    {
        RequireLinux();
        using var cancellation = new CancellationTokenSource();
        var service = new LinuxEnvironmentDiagnosticService(new CancellingDiscovery(cancellation));

        var action = async () => await service.InspectAsync(new([GameVariant.XCom2], "/tmp"), cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    public TestContext TestContext { get; set; }

    private static SteamGameDiscovery Discovery(Fixture? fixture)
    {
        var applications = fixture is null
            ? Array.Empty<SteamInstalledApplication>()
            : [new SteamInstalledApplication(SteamAppId.Xcom2, fixture.Root, Path.Combine(fixture.SteamApps, "appmanifest_268500.acf"), "XCOM 2", fixture.Game, "XCOM 2", "4", true, true)];
        var workshop = fixture is null ? Array.Empty<SteamWorkshopLocation>() : [new SteamWorkshopLocation(SteamAppId.Xcom2, fixture.Root, fixture.Workshop, [])];
        var prefixes = fixture is null ? Array.Empty<ProtonPrefix>() : [new ProtonPrefix(SteamAppId.Xcom2, fixture.Root, Path.Combine(fixture.SteamApps, "compatdata"), fixture.Prefix, fixture.Prefix, true, true, ["steamuser"])];
        return new(SteamAppId.Xcom2, [], [], applications, workshop, prefixes, [], []);
    }

    private static Fixture CreateFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "aaml-linux-environment", Guid.NewGuid().ToString("N"));
        var steamApps = Path.Combine(root, "steamapps");
        var game = Path.Combine(steamApps, "common", "XCOM 2");
        var workshop = Path.Combine(steamApps, "workshop", "content", "268500");
        var prefix = Path.Combine(steamApps, "compatdata", "268500", "pfx");
        var user = Path.Combine(prefix, "drive_c", "users", "steamuser");
        var config = Path.Combine(user, "documents", "my games", "XCOM2", "XComGame", "Config");
        var wotcConfig = Path.Combine(user, "documents", "my games", "XCOM2 War of the Chosen", "XComGame", "Config");
        Directory.CreateDirectory(Path.Combine(game, "Binaries", "Win64"));
        Directory.CreateDirectory(Path.Combine(game, "XCom2-WarOfTheChosen", "Binaries", "Win64"));
        Directory.CreateDirectory(workshop);
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(wotcConfig);
        File.WriteAllBytes(Path.Combine(game, "Binaries", "Win64", "XCom2.exe"), []);
        File.WriteAllBytes(Path.Combine(game, "XCom2-WarOfTheChosen", "Binaries", "Win64", "XCom2.exe"), []);
        File.WriteAllText(Path.Combine(steamApps, "appmanifest_268500.acf"), "\"AppState\" { \"appid\" \"268500\" \"installdir\" \"XCOM 2\" }");
        File.WriteAllText(Path.Combine(config, "sentinel.ini"), "vanilla");
        File.WriteAllText(Path.Combine(wotcConfig, "sentinel.ini"), "wotc");
        return new(root, steamApps, game, workshop, prefix, config);
    }

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("Linux environment diagnostics require Linux.");
    }

    private sealed class RecordingDiscovery(SteamGameDiscovery result) : ISteamFilesystemDiscovery
    {
        public SteamDiscoveryRequest? LastRequest { get; private set; }
        public Task<Result<SteamGameDiscovery>> DiscoverAsync(SteamDiscoveryRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(Result<SteamGameDiscovery>.Success(result));
        }
    }

    private sealed class CancellingDiscovery(CancellationTokenSource cancellation) : ISteamFilesystemDiscovery
    {
        public Task<Result<SteamGameDiscovery>> DiscoverAsync(SteamDiscoveryRequest request, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.FromResult(Result<SteamGameDiscovery>.Success(Discovery(null)));
        }
    }

    private sealed record Fixture(string Root, string SteamApps, string Game, string Workshop, string Prefix, string Config);
}
