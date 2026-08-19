using AAML.Application.Configurations;
using AAML.Domain.Games;
using AAML.Infrastructure.Linux.Launching;
using FluentAssertions;

namespace AAML.Infrastructure.Linux.Tests;

[TestClass]
public sealed class LinuxLegacyGameConfigurationSourceTests
{
    [TestMethod]
    public void CapabilitiesExposeOnlyQualifiedLinuxMigrationActions()
    {
        var capabilities = new LinuxLegacyGameConfigurationSource().Capabilities;

        capabilities.CanReadActiveMods.Should().BeFalse();
        capabilities.CanReadModRoots.Should().BeTrue();
        capabilities.CanCleanupOverrides.Should().BeFalse();
        capabilities.Guidance.Should().Contain("Windows-only").And.Contain("Proton");
        var source = new LinuxLegacyGameConfigurationSource();
        source.SupportsModRoots(GameVariant.XCom2).Should().BeTrue();
        source.SupportsModRoots(GameVariant.XCom2WarOfTheChosen).Should().BeTrue();
        source.SupportsModRoots(GameVariant.ChimeraSquad).Should().BeFalse();
    }

    [TestMethod]
    public async Task ChimeraIsExplicitlyUnsupportedWithoutClaimingProtonBehavior()
    {
        var result = await new LinuxLegacyGameConfigurationSource().ReadModRootsAsync(GameVariant.ChimeraSquad, "/games/chimera", [], TestContext.CancellationToken);
        result.Error!.Code.Should().Be("mod_roots.variant_unsupported");
        result.Error.Message.Should().Contain("Chimera Squad is not supported");
    }

    [TestMethod]
    public async Task SupportedProtonPreviewReportsExactDriveMappingsAndPreservesSource()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Exact Proton path behavior runs on Linux."); return; }
        var root = Path.Combine(Path.GetTempPath(), "aaml-proton-roots", Guid.NewGuid().ToString("N"));
        var steamApps = Path.Combine(root, "steamapps"); var game = Path.Combine(steamApps, "common", "XCOM 2");
        var binary = Path.Combine(game, "Binaries", "Win64"); var workshop = Path.Combine(steamApps, "workshop", "content", "268500");
        var config = Path.Combine(steamApps, "compatdata", "268500", "pfx", "drive_c", "users", "steamuser", "Documents", "My Games", "XCOM2", "XComGame", "Config");
        try
        {
            Directory.CreateDirectory(binary); Directory.CreateDirectory(workshop); Directory.CreateDirectory(config);
            await File.WriteAllTextAsync(Path.Combine(steamApps, "appmanifest_268500.acf"), "\"AppState\" { \"appid\" \"268500\" \"installdir\" \"XCOM 2\" }");
            await File.WriteAllTextAsync(Path.Combine(binary, "XCom2.exe"), string.Empty, TestContext.CancellationToken);
            var engine = Path.Combine(config, "XComEngine.ini"); var original = "[Engine.DownloadableContentEnumerator]\nModRootDirs=S:\\workshop\\content\\268500\\\n";
            await File.WriteAllTextAsync(engine, original, TestContext.CancellationToken);

            var preview = await new LinuxLegacyGameConfigurationSource().ReadModRootsAsync(GameVariant.XCom2, game, [], TestContext.CancellationToken);

            preview.IsSuccess.Should().BeTrue(preview.Error?.Message);
            preview.Value!.Rows.Single().ResolvedPath.Should().Be(workshop);
            preview.Value.PlatformBehavior.Should().Contain("S: maps to").And.Contain("Z: maps to /").And.Contain("Chimera Squad is not supported");
            (await File.ReadAllTextAsync(engine, TestContext.CancellationToken)).Should().Be(original);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public TestContext TestContext { get; set; }
}
