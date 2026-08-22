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

    [TestMethod]
    public async Task ManifestInstallDirectory_UsesUniqueCaseFallback()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("Filesystem Steam discovery requires Linux.");
        var root = Path.Combine(Path.GetTempPath(), "AAML.SteamDiscovery", Guid.NewGuid().ToString("N"));
        try
        {
            var actualGame = Path.Combine(root, "steamapps", "common", "xcom 2");
            var actualWorkshop = Path.Combine(root, "steamapps", "Workshop", "Content", "268500");
            var actualPrefix = Path.Combine(root, "steamapps", "CompatData", "268500", "PFX");
            Directory.CreateDirectory(actualGame);
            Directory.CreateDirectory(actualWorkshop);
            Directory.CreateDirectory(Path.Combine(actualPrefix, "Drive_C", "Users", "steamuser"));
            await File.WriteAllTextAsync(Path.Combine(root, "steamapps", "appmanifest_268500.acf"), "\"AppState\" { \"appid\" \"268500\" \"installdir\" \"XCOM 2\" }");

            var result = await new LinuxSteamFilesystemDiscovery(new LinuxPhysicalPathResolver())
                .DiscoverAsync(new SteamDiscoveryRequest([SteamAppId.Xcom2], [root]), TestContext.CancellationToken);

            result.Value!.Applications.Should().ContainSingle(application => application.InstallDirectoryExists && application.GameInstallPath == actualGame);
            result.Value.WorkshopLocations.Should().ContainSingle(location => location.ContentRootPath == actualWorkshop);
            result.Value.ProtonPrefixes.Should().ContainSingle(prefix => prefix.Exists && prefix.PrefixPath == actualPrefix && prefix.WineUsers.Contains("steamuser"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ManifestInstallDirectory_AmbiguousFallbackIsRejectedWithDiagnostic()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("Filesystem Steam discovery requires Linux.");
        var root = Path.Combine(Path.GetTempPath(), "AAML.SteamDiscovery", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "steamapps", "common", "XCOM 2"));
            Directory.CreateDirectory(Path.Combine(root, "steamapps", "common", "xcom 2"));
            await File.WriteAllTextAsync(Path.Combine(root, "steamapps", "appmanifest_268500.acf"), "\"AppState\" { \"appid\" \"268500\" \"installdir\" \"XcOm 2\" }");

            var result = await new LinuxSteamFilesystemDiscovery(new LinuxPhysicalPathResolver())
                .DiscoverAsync(new SteamDiscoveryRequest([SteamAppId.Xcom2], [root]), TestContext.CancellationToken);

            result.Value!.Applications.Should().ContainSingle(application => !application.InstallDirectoryExists);
            result.Value.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "path.known_artifact_case_ambiguous" && diagnostic.Message.Contains("XCOM 2", StringComparison.Ordinal));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ManifestInstallDirectory_WithOnlySeparatorsIsInvalid()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("Filesystem Steam discovery requires Linux.");
        var root = Path.Combine(Path.GetTempPath(), "AAML.SteamDiscovery", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "steamapps", "common"));
            await File.WriteAllTextAsync(Path.Combine(root, "steamapps", "appmanifest_268500.acf"), "\"AppState\" { \"appid\" \"268500\" \"installdir\" \"\\\\\" }");

            var result = await new LinuxSteamFilesystemDiscovery(new LinuxPhysicalPathResolver())
                .DiscoverAsync(new SteamDiscoveryRequest([SteamAppId.Xcom2], [root]), TestContext.CancellationToken);

            result.Value!.Applications.Should().BeEmpty();
            result.Value.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "steam.install_dir_invalid");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task SteamAppsSymlinkOutsideExplicitRootIsRejected()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("Filesystem Steam discovery requires Linux.");
        var root = Path.Combine(Path.GetTempPath(), "AAML.SteamDiscovery", Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "AAML.SteamDiscovery", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(outside);
            Directory.CreateSymbolicLink(Path.Combine(root, "steamapps"), outside);

            var result = await new LinuxSteamFilesystemDiscovery(new LinuxPhysicalPathResolver())
                .DiscoverAsync(new SteamDiscoveryRequest([SteamAppId.Xcom2], [root]), TestContext.CancellationToken);

            result.Error!.Code.Should().Be("path.known_artifact_outside_root");
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(outside, true);
        }
    }

    public TestContext TestContext { get; set; }
}
