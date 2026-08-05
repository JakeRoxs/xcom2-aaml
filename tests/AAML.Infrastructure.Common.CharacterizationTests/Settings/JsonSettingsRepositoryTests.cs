using AAML.Application.Settings;
using AAML.Domain.Games;
using AAML.Domain.Launching;
using AAML.Domain.Mods;
using AAML.Infrastructure.Common.Settings;
using FluentAssertions;
using System.Text;

namespace AAML.Infrastructure.Common.CharacterizationTests.Settings;

[TestClass]
public sealed class JsonSettingsRepositoryTests
{
    [TestMethod]
    public async Task SaveLoadAndBackup_AreAtomicTelemetryFreeAndCurrentDirectoryIndependent()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML.Settings", Guid.NewGuid().ToString("N"));
        var paths = new TestPaths(root);
        var repository = new JsonSettingsRepository(paths);
        var first = Settings("C:\\Games\\XCOM 2");
        var second = Settings("C:\\Games\\XCOM 2 Updated");
        try
        {
            (await repository.SaveAsync(first, TestContext.CancellationToken)).IsSuccess.Should().BeTrue();
            (await repository.SaveAsync(second, TestContext.CancellationToken)).IsSuccess.Should().BeTrue();

            var loaded = await repository.LoadAsync(TestContext.CancellationToken);
            loaded.Value.Should().BeEquivalentTo(second);
            File.Exists(Path.Combine(paths.ConfigurationDirectory, "settings.json.bak")).Should().BeTrue();
            var json = await File.ReadAllTextAsync(Path.Combine(paths.ConfigurationDirectory, "settings.json"), TestContext.CancellationToken);
            json.Should().NotContainEquivalentOf("sentry").And.NotContainEquivalentOf("telemetry").And.NotContain("Guid").And.NotContain("UserName");
            Directory.EnumerateFiles(paths.ConfigurationDirectory, "*.tmp").Should().BeEmpty();
            var bytes = await File.ReadAllBytesAsync(Path.Combine(paths.ConfigurationDirectory, "settings.json"), TestContext.CancellationToken);
            Convert.ToHexString(bytes.AsSpan(0, Math.Min(3, bytes.Length))).Should().NotBe(Convert.ToHexString(Encoding.UTF8.GetPreamble()));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task Backup_ContainsPreviousGenerationAndRecoversCorruptPrimary()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML.Settings", Guid.NewGuid().ToString("N"));
        var paths = new TestPaths(root);
        var repository = new JsonSettingsRepository(paths);
        var first = Settings("C:\\Games\\First");
        var second = Settings("C:\\Games\\Second");
        try
        {
            (await repository.SaveAsync(first, TestContext.CancellationToken)).IsSuccess.Should().BeTrue();
            (await repository.SaveAsync(second, TestContext.CancellationToken)).IsSuccess.Should().BeTrue();
            await File.WriteAllTextAsync(Path.Combine(paths.ConfigurationDirectory, "settings.json"), "{ corrupt", TestContext.CancellationToken);

            var recovered = await repository.LoadAsync(TestContext.CancellationToken);

            recovered.IsSuccess.Should().BeTrue();
            recovered.Value.Should().BeEquivalentTo(first);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task PreCancelledLoad_PreservesCancellationInsteadOfRecoveryFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML.Settings", Guid.NewGuid().ToString("N"));
        var repository = new JsonSettingsRepository(new TestPaths(root));
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        var result = await repository.LoadAsync(source.Token);

        result.Error!.Code.Should().Be("settings.cancelled");
    }

    [TestMethod]
    public async Task VersionOneEmptyArguments_MigrateToLegacySafeDefaults()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML.Settings", Guid.NewGuid().ToString("N"));
        var paths = new TestPaths(root);
        try
        {
            Directory.CreateDirectory(paths.ConfigurationDirectory);
            await File.WriteAllTextAsync(Path.Combine(paths.ConfigurationDirectory, "settings.json"), """
                { "schemaVersion": 1, "selectedGame": "XCom2", "modRootLocations": [], "launchArguments": [], "modIntents": [], "categories": [], "tags": [] }
                """, TestContext.CancellationToken);

            var loaded = await new JsonSettingsRepository(paths).LoadAsync(TestContext.CancellationToken);

            loaded.Value!.SchemaVersion.Should().Be(ApplicationSettingsDefaults.CurrentSchemaVersion);
            loaded.Value.LaunchArguments.Select(argument => argument.Value).Should().Equal("-review", "-noRedScreens");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task VersionTwoEmptyArguments_RemainAnExplicitUserChoice()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML.Settings", Guid.NewGuid().ToString("N"));
        var paths = new TestPaths(root);
        try
        {
            Directory.CreateDirectory(paths.ConfigurationDirectory);
            await File.WriteAllTextAsync(Path.Combine(paths.ConfigurationDirectory, "settings.json"), """
                { "schemaVersion": 2, "selectedGame": "XCom2", "modRootLocations": [], "launchArguments": [], "modIntents": [], "categories": [], "tags": [] }
                """, TestContext.CancellationToken);

            var loaded = await new JsonSettingsRepository(paths).LoadAsync(TestContext.CancellationToken);

            loaded.Value!.LaunchArguments.Should().BeEmpty();
            loaded.Value.AllowLaunchWithMissingDependencies.Should().BeFalse();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public TestContext TestContext { get; set; }

    private static ApplicationSettings Settings(string gamePath) => new(
        ApplicationSettingsDefaults.CurrentSchemaVersion,
        GameVariant.XCom2,
        gamePath,
        ["C:\\Mods"],
        [new LaunchArgument("-Name=Unicode Ω")],
        [],
        [],
        [],
        true,
        new Dictionary<GameVariant, GameLocationSettings>
        {
            [GameVariant.XCom2] = new(gamePath, ["C:\\Mods"]),
            [GameVariant.ChimeraSquad] = new("I:\\Chimera", [])
        },
        true,
        WorkshopStartupRefreshPolicy.ActiveMods,
        ThemePreference.Dark,
        true,
        [new DuplicatePreference(new AAML.Domain.Mods.PackageId("SharedPackage"), new AAML.Domain.Mods.ModKey(AAML.Domain.Mods.ModSource.Manual, "C:\\Mods\\Preferred"))],
        new ModGridPreferences(false, AAML.Application.Mods.Grid.ModGridSemanticState.Duplicate, true, new HashSet<AAML.Application.Mods.Grid.ModGridGroupKey> { new("category", "gameplay") }),
        []);

    private sealed class TestPaths(string root) : AAML.Application.Ports.IApplicationPaths
    {
        public string ConfigurationDirectory { get; } = Path.Combine(root, "Config");
        public string DataDirectory { get; } = Path.Combine(root, "Data");
        public string StateDirectory { get; } = Path.Combine(root, "State");
        public string CacheDirectory { get; } = Path.Combine(root, "Cache");
        public string RuntimeDirectory { get; } = Path.Combine(root, "Runtime");
    }
}
