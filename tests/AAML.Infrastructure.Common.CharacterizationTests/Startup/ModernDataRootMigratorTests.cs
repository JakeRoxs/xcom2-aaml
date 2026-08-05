using AAML.Application.Ports;
using AAML.Infrastructure.Common.Startup;
using FluentAssertions;

namespace AAML.Infrastructure.Common.CharacterizationTests.Startup;

[TestClass]
public sealed class ModernDataRootMigratorTests
{
    [TestMethod]
    public void FormerDurableData_IsCopiedWithoutChangingSourceOrMovingRuntime()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML data migration", Guid.NewGuid().ToString("N"));
        var former = new TestPaths(Path.Combine(root, "former")); var current = new TestPaths(Path.Combine(root, "current"));
        var settings = Path.Combine(former.ConfigurationDirectory, "settings.json"); var profile = Path.Combine(former.DataDirectory, "Profiles", "profiles.json"); var runtime = Path.Combine(former.RuntimeDirectory, "steam-launch", "request.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!); Directory.CreateDirectory(Path.GetDirectoryName(profile)!); Directory.CreateDirectory(Path.GetDirectoryName(runtime)!);
        File.WriteAllText(settings, "settings-v6"); File.WriteAllText(profile, "profiles"); File.WriteAllText(runtime, "secret-runtime-request");
        try
        {
            var receipt = ModernDataRootMigrator.Migrate(former, current, TestContext.CancellationToken);

            receipt.Status.Should().Be(DataRootMigrationStatus.Completed);
            File.ReadAllText(Path.Combine(current.ConfigurationDirectory, "settings.json")).Should().Be("settings-v6");
            File.ReadAllText(Path.Combine(current.DataDirectory, "Profiles", "profiles.json")).Should().Be("profiles");
            File.ReadAllText(settings).Should().Be("settings-v6");
            File.Exists(Path.Combine(current.RuntimeDirectory, "steam-launch", "request.json")).Should().BeFalse();
            File.Exists(Path.Combine(current.StateDirectory, "Migrations", "modern-data-root-v1.json")).Should().BeTrue();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ExistingDifferentDestination_WinsAndIsReportedIdempotently()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML data conflict", Guid.NewGuid().ToString("N"));
        var former = new TestPaths(Path.Combine(root, "former")); var current = new TestPaths(Path.Combine(root, "current"));
        var source = Path.Combine(former.ConfigurationDirectory, "settings.json"); var destination = Path.Combine(current.ConfigurationDirectory, "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.WriteAllText(source, "former"); File.WriteAllText(destination, "current");
        try
        {
            var first = ModernDataRootMigrator.Migrate(former, current, TestContext.CancellationToken);
            var second = ModernDataRootMigrator.Migrate(former, current, TestContext.CancellationToken);

            first.Status.Should().Be(DataRootMigrationStatus.CompletedWithConflicts);
            second.Status.Should().Be(DataRootMigrationStatus.CompletedWithConflicts);
            first.Items.Single(item => item.Id == "settings").Outcome.Should().Be(DataRootMigrationOutcome.Conflict);
            File.ReadAllText(destination).Should().Be("current"); File.ReadAllText(source).Should().Be("former");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public TestContext TestContext { get; set; }
    private sealed class TestPaths(string root) : IApplicationPaths
    {
        public string ConfigurationDirectory { get; } = Path.Combine(root, "Config");
        public string DataDirectory { get; } = Path.Combine(root, "Data");
        public string StateDirectory { get; } = Path.Combine(root, "State");
        public string CacheDirectory { get; } = Path.Combine(root, "Cache");
        public string RuntimeDirectory { get; } = Path.Combine(root, "Runtime");
    }
}
