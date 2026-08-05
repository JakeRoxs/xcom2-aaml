using AAML.Application.Steam;
using AAML.Infrastructure.Windows.Steam;
using FluentAssertions;

namespace AAML.Infrastructure.Windows.Tests;

[TestClass]
public sealed class WindowsSteamFilesystemDiscoveryTests
{
    [TestMethod]
    public async Task SecondaryLibrary_DiscoversInstalledGameAndWorkshopRoot()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows Steam filesystem paths require Windows.");
        var root = Path.Combine(Path.GetTempPath(), "AAML Steam Ω", Guid.NewGuid().ToString("N"));
        var secondary = Path.Combine(root, "Secondary Library");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "steamapps"));
            Directory.CreateDirectory(Path.Combine(secondary, "steamapps", "common", "XCOM 2"));
            Directory.CreateDirectory(Path.Combine(secondary, "steamapps", "workshop", "content", "268500", "900000001"));
            await File.WriteAllTextAsync(Path.Combine(root, "steamapps", "libraryfolders.vdf"), $"\"libraryfolders\" {{ \"0\" {{ \"path\" \"{Escape(root)}\" }} \"1\" {{ \"path\" \"{Escape(secondary)}\" }} }}", TestContext.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(secondary, "steamapps", "appmanifest_268500.acf"), "\"AppState\" { \"appid\" \"268500\" \"name\" \"XCOM 2\" \"installdir\" \"XCOM 2\" }", TestContext.CancellationToken);

            var result = await new WindowsSteamFilesystemDiscovery(new FixedRootLocator(root)).DiscoverAsync(new SteamDiscoveryRequest([SteamAppId.Xcom2]), TestContext.CancellationToken);

            result.IsSuccess.Should().BeTrue();
            result.Value!.Applications.Should().ContainSingle(application => application.InstallDirectoryExists && application.GameInstallPath == Path.Combine(secondary, "steamapps", "common", "XCOM 2"));
            result.Value.WorkshopLocations.Single().ExistingItemDirectories.Should().ContainSingle();
            result.Value.ProtonPrefixes.Should().BeEmpty();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task TwoInstalledCopies_ReportAmbiguityWithoutSelectingByOrder()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows Steam filesystem paths require Windows.");
        var root = Path.Combine(Path.GetTempPath(), "AAML Steam", Guid.NewGuid().ToString("N"));
        var secondary = Path.Combine(root, "Secondary");
        try
        {
            foreach (var library in new[] { root, secondary })
            {
                Directory.CreateDirectory(Path.Combine(library, "steamapps", "common", "XCOM 2"));
                await File.WriteAllTextAsync(Path.Combine(library, "steamapps", "appmanifest_268500.acf"), "\"AppState\" { \"appid\" \"268500\" \"installdir\" \"XCOM 2\" }", TestContext.CancellationToken);
            }
            await File.WriteAllTextAsync(Path.Combine(root, "steamapps", "libraryfolders.vdf"), $"\"libraryfolders\" {{ \"0\" {{ \"path\" \"{Escape(root)}\" }} \"1\" {{ \"path\" \"{Escape(secondary)}\" }} }}", TestContext.CancellationToken);

            var result = await new WindowsSteamFilesystemDiscovery(new FixedRootLocator(root)).DiscoverAsync(new SteamDiscoveryRequest([SteamAppId.Xcom2]), TestContext.CancellationToken);

            result.Value!.Applications.Count(application => application.InstallDirectoryExists).Should().Be(2);
            result.Value.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "steam.game_install_ambiguous");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public TestContext TestContext { get; set; }

    private static string Escape(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);
    private sealed class FixedRootLocator(string root) : IWindowsSteamRootLocator
    {
        public IReadOnlyList<string> GetRoots() => [root];
    }
}
