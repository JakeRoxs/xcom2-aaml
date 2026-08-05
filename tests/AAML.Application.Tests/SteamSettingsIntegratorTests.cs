using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Application.Settings;
using AAML.Application.Startup;
using AAML.Application.Steam;
using AAML.Domain.Games;
using FluentAssertions;

namespace AAML.Application.Tests;

[TestClass]
public sealed class SteamSettingsIntegratorTests
{
    [TestMethod]
    public async Task OneInstalledCopy_PersistsGameAndExistingWorkshopWithoutRemovingManualRoots()
    {
        var workshop = Path.Combine(Path.GetTempPath(), "AAML Workshop", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workshop);
            var snapshot = Snapshot([Application("G:\\SteamLibrary\\steamapps\\common\\XCOM 2", true)], [new SteamWorkshopLocation(SteamAppId.Xcom2, "G:\\SteamLibrary", workshop, [])]);
            var repository = new RecordingRepository();
            var integrator = new SteamSettingsIntegrator(new FixedDiscovery(snapshot), repository);

            var result = await integrator.DiscoverAndApplyAsync(Settings(["D:\\Manual Mods"]), TestContext.CancellationToken);

            result.Value!.Settings.GameInstallationLocation.Should().Be("G:\\SteamLibrary\\steamapps\\common\\XCOM 2");
            result.Value.Settings.ModRootLocations.Should().Equal("D:\\Manual Mods", workshop);
            repository.Saved.Should().Be(result.Value.Settings);
        }
        finally { if (Directory.Exists(workshop)) Directory.Delete(workshop, true); }
    }

    [TestMethod]
    public async Task AmbiguousInstalledCopies_FailWithoutPersistingEitherCopy()
    {
        var snapshot = Snapshot([Application("C:\\XCOM 2", true), Application("G:\\XCOM 2", true)], []);
        var repository = new RecordingRepository();

        var result = await new SteamSettingsIntegrator(new FixedDiscovery(snapshot), repository).DiscoverAndApplyAsync(Settings([]), TestContext.CancellationToken);

        result.Error!.Code.Should().Be("steam.game_install_ambiguous");
        repository.Saved.Should().BeNull();
    }

    [TestMethod]
    public async Task ChimeraDiscovery_Uses882100AndReplacesXcomWorkshopRootsWithoutPathLeakage()
    {
        var workshop = Path.Combine(Path.GetTempPath(), "AAML Chimera Workshop", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workshop);
            var app = new SteamInstalledApplication(SteamAppId.ChimeraSquad, "I:\\SteamLibrary", "appmanifest_882100.acf", "XCOM-Chimera-Squad", "I:\\SteamLibrary\\steamapps\\common\\XCOM-Chimera-Squad", "XCOM: Chimera Squad", "4", true, true);
            var discovery = new FixedDiscovery(new SteamGameDiscovery(SteamAppId.ChimeraSquad, [], [], [app], [new SteamWorkshopLocation(SteamAppId.ChimeraSquad, "I:\\SteamLibrary", workshop, [])], [], [], []));
            var settings = Settings(["D:\\Manual Mods", "G:\\SteamLibrary\\steamapps\\workshop\\content\\268500"]) with { SelectedGame = GameVariant.ChimeraSquad };

            var result = await new SteamSettingsIntegrator(discovery, new RecordingRepository()).DiscoverAndApplyAsync(settings, TestContext.CancellationToken);

            discovery.Request!.AppIds.Should().Equal(SteamAppId.ChimeraSquad);
            result.Value!.Settings.GameInstallationLocation.Should().EndWith("XCOM-Chimera-Squad");
            result.Value.Settings.ModRootLocations.Should().Equal("D:\\Manual Mods", workshop);
        }
        finally { if (Directory.Exists(workshop)) Directory.Delete(workshop, true); }
    }

    public TestContext TestContext { get; set; }

    private static ApplicationSettings Settings(IReadOnlyList<string> roots) => new(ApplicationSettingsDefaults.CurrentSchemaVersion, GameVariant.XCom2, null, roots, ApplicationSettingsDefaults.LaunchArguments, [], [], []);
    private static SteamInstalledApplication Application(string path, bool exists)
    {
        var marker = path.IndexOf("\\steamapps", StringComparison.OrdinalIgnoreCase);
        var library = marker >= 0 ? path[..marker] : Path.GetPathRoot(path) ?? path;
        return new SteamInstalledApplication(SteamAppId.Xcom2, library, "appmanifest_268500.acf", "XCOM 2", path, "XCOM 2", "4", true, exists);
    }
    private static SteamGameDiscovery Snapshot(IReadOnlyList<SteamInstalledApplication> applications, IReadOnlyList<SteamWorkshopLocation> workshops) => new(SteamAppId.Xcom2, [], [], applications, workshops, [], [], []);

    private sealed class FixedDiscovery(SteamGameDiscovery snapshot) : ISteamFilesystemDiscovery
    {
        public SteamDiscoveryRequest? Request { get; private set; }
        public Task<Result<SteamGameDiscovery>> DiscoverAsync(SteamDiscoveryRequest request, CancellationToken cancellationToken) { Request = request; return Task.FromResult(Result<SteamGameDiscovery>.Success(snapshot)); }
    }

    private sealed class RecordingRepository : ISettingsRepository
    {
        public ApplicationSettings? Saved { get; private set; }
        public Task<Result<ApplicationSettings>> LoadAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Result> SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken) { Saved = settings; return Task.FromResult(Result.Success()); }
    }
}
