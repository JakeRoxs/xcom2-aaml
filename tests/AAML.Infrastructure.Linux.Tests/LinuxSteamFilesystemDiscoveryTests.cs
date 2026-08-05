using AAML.Application.Steam;
using AAML.Infrastructure.Linux.Paths;
using AAML.Infrastructure.Linux.Steam;
using FluentAssertions;

namespace AAML.Infrastructure.Linux.Tests;

[TestClass]
public sealed class LinuxSteamFilesystemDiscoveryTests
{
    [TestMethod]
    public async Task ExplicitFixtureRoot_DiscoversGameWorkshopAndPrefix()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("Filesystem Steam discovery requires Linux.");
        var root = Path.Combine(Path.GetTempPath(), "AAML.SteamDiscovery", Guid.NewGuid().ToString("N"));
        try
        {
            var steamApps = Directory.CreateDirectory(Path.Combine(root, "steamapps"));
            Directory.CreateDirectory(Path.Combine(root, "steamapps", "common", "XCOM 2"));
            Directory.CreateDirectory(Path.Combine(root, "steamapps", "workshop", "content", "268500", "900000001"));
            Directory.CreateDirectory(Path.Combine(root, "steamapps", "compatdata", "268500", "pfx", "drive_c", "users", "steamuser"));
            await File.WriteAllTextAsync(Path.Combine(steamApps.FullName, "libraryfolders.vdf"), $"\"libraryfolders\" {{ \"0\" {{ \"path\" \"{root.Replace("\\", "/")}\" }} }}");
            await File.WriteAllTextAsync(Path.Combine(steamApps.FullName, "appmanifest_268500.acf"), "\"AppState\" { \"appid\" \"268500\" \"name\" \"XCOM 2\" \"installdir\" \"XCOM 2\" }");

            var discovery = new LinuxSteamFilesystemDiscovery(new LinuxPhysicalPathResolver());
            var result = await discovery.DiscoverAsync(new SteamDiscoveryRequest([SteamAppId.Xcom2], [root]), TestContext.CancellationToken);

            result.IsSuccess.Should().BeTrue();
            result.Value!.Applications.Should().ContainSingle(application => application.InstallDirectoryExists);
            result.Value.WorkshopLocations.Single().ExistingItemDirectories.Should().ContainSingle();
            result.Value.ProtonPrefixes.Single().WineUsers.Should().ContainSingle("steamuser");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task SecondaryLibrary_WithSecondInstalledCopy_ReportsAmbiguity()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("Filesystem Steam discovery requires Linux.");
        var root = Path.Combine(Path.GetTempPath(), "AAML.SteamDiscovery", Guid.NewGuid().ToString("N"));
        var secondary = Path.Combine(root, "secondary");
        try
        {
            foreach (var library in new[] { root, secondary })
            {
                Directory.CreateDirectory(Path.Combine(library, "steamapps", "common", "XCOM 2"));
                await File.WriteAllTextAsync(Path.Combine(library, "steamapps", "appmanifest_268500.acf"), "\"AppState\" { \"appid\" \"268500\" \"installdir\" \"XCOM 2\" }");
            }
            Directory.CreateDirectory(Path.Combine(root, "steamapps"));
            await File.WriteAllTextAsync(Path.Combine(root, "steamapps", "libraryfolders.vdf"), $"\"libraryfolders\" {{ \"0\" {{ \"path\" \"{root}\" }} \"1\" {{ \"path\" \"{secondary}\" }} }}");

            var result = await new LinuxSteamFilesystemDiscovery(new LinuxPhysicalPathResolver()).DiscoverAsync(new SteamDiscoveryRequest([SteamAppId.Xcom2], [root]), TestContext.CancellationToken);

            result.IsSuccess.Should().BeTrue();
            result.Value!.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "steam.game_install_ambiguous");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public TestContext TestContext { get; set; }
}
