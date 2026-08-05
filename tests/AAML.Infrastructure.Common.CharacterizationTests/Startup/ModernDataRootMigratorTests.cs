using AAML.Application.Ports;
using AAML.Infrastructure.Common.Startup;
using FluentAssertions;
using Newtonsoft.Json.Linq;

namespace AAML.Infrastructure.Common.CharacterizationTests.Startup;

[TestClass]
public sealed class ModernDataRootMigratorTests
{
    public static IEnumerable<object[]> InterruptionPoints => Enumerable.Range(1, 12).Select(value => new object[] { value });

    [TestMethod]
    public void FormerDurableData_IsCopiedWithoutChangingSourceOrMovingRuntime()
    {
        using var scope = new MigrationScope("AAML data migration");
        var settings = scope.Source("Config", "settings.json");
        var profile = scope.Source("Data", "Profiles", "profiles.json");
        var runtime = scope.Source("Runtime", "steam-launch", "request.json");
        Write(settings, "settings-v6");
        Write(profile, "profiles");
        Write(runtime, "secret-runtime-request");

        var receipt = scope.Migrate();

        receipt.SchemaVersion.Should().Be(2);
        receipt.ExpectedManifestVersion.Should().Be(1);
        receipt.ExpectedManifestCount.Should().Be(12);
        receipt.Status.Should().Be(DataRootMigrationStatus.Completed);
        receipt.StartedAtUtc.Should().BeBefore(receipt.CompletedAtUtc!.Value);
        receipt.Items.Should().HaveCount(12);
        File.ReadAllText(scope.Destination("Config", "settings.json")).Should().Be("settings-v6");
        File.ReadAllText(scope.Destination("Data", "Profiles", "profiles.json")).Should().Be("profiles");
        File.ReadAllText(settings).Should().Be("settings-v6");
        File.Exists(scope.Destination("Runtime", "steam-launch", "request.json")).Should().BeFalse();
    }

    [TestMethod]
    [DynamicData(nameof(InterruptionPoints))]
    public void InterruptionAfterEveryItem_LeavesStartedReceiptAndResumes(int interruptionPoint)
    {
        using var scope = new MigrationScope("AAML interruption");
        WriteAllManifestSources(scope);
        var interruption = new DataRootMigrationTestHooks(count =>
        {
            if (count == interruptionPoint) throw new SimulatedInterruptionException();
        });

        Action firstRun = () => scope.Migrate(CancellationToken.None, interruption);

        firstRun.Should().Throw<SimulatedInterruptionException>();
        var partial = scope.ReadReceipt();
        partial.Value<int>("schemaVersion").Should().Be(2);
        partial.Value<string>("status").Should().Be("Started");
        partial["completedAtUtc"]!.Type.Should().Be(JTokenType.Null);
        partial["items"]!.Should().HaveCount(interruptionPoint);

        var resumed = scope.Migrate();
        resumed.Status.Should().Be(DataRootMigrationStatus.Completed);
        resumed.Items.Should().HaveCount(12);
        resumed.Items.Should().OnlyContain(item => item.Outcome == DataRootMigrationOutcome.Copied || item.Outcome == DataRootMigrationOutcome.SourceMissing);
    }

    [TestMethod]
    public void Cancellation_PreservesIncompleteProgressForResume()
    {
        using var scope = new MigrationScope("AAML cancellation");
        WriteAllManifestSources(scope);
        using var cancellation = new CancellationTokenSource();
        var hooks = new DataRootMigrationTestHooks(count =>
        {
            if (count == 5) cancellation.Cancel();
        });

        Action firstRun = () => scope.Migrate(cancellation.Token, hooks);

        firstRun.Should().Throw<OperationCanceledException>();
        scope.ReadReceipt().Value<string>("status").Should().Be("Started");
        scope.ReadReceipt()["items"]!.Should().HaveCount(5);

        scope.Migrate().Status.Should().Be(DataRootMigrationStatus.Completed);
    }

    [TestMethod]
    public void LockContention_FailsClosedWithoutChangingReceipt()
    {
        using var scope = new MigrationScope("AAML lock");
        scope.Migrate();
        var before = File.ReadAllText(scope.ReceiptPath);
        using var heldLock = new FileStream(scope.LockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Action act = () => scope.Migrate();

        act.Should().Throw<IOException>().WithMessage("*already running*");
        File.ReadAllText(scope.ReceiptPath).Should().Be(before);
    }

    [TestMethod]
    public void DurableFailure_IsFailedAndCanResumeAfterRecovery()
    {
        using var scope = new MigrationScope("AAML durable failure");
        Write(scope.Source("Data", "Profiles", "profiles.json"), "profiles");
        Write(scope.Destination("Data", "Profiles"), "blocks-directory");

        var failed = scope.Migrate();

        failed.Status.Should().Be(DataRootMigrationStatus.Failed);
        failed.Items.Single(item => item.Id == "profiles").Outcome.Should().Be(DataRootMigrationOutcome.Failed);

        File.Delete(scope.Destination("Data", "Profiles"));
        var resumed = scope.Migrate();
        resumed.Status.Should().Be(DataRootMigrationStatus.Completed);
        File.ReadAllText(scope.Destination("Data", "Profiles", "profiles.json")).Should().Be("profiles");
    }

    [TestMethod]
    public void OptionalFailure_DoesNotBlockCompletion()
    {
        using var scope = new MigrationScope("AAML optional failure");
        Write(scope.Source("State", "Logs", "aaml.log"), "log");
        Write(scope.Destination("State", "Logs"), "blocks-directory");

        var receipt = scope.Migrate();

        receipt.Status.Should().Be(DataRootMigrationStatus.Completed);
        receipt.Items.Single(item => item.Id == "log").Should().Match<DataRootMigrationItem>(item => !item.Durable && item.Outcome == DataRootMigrationOutcome.Failed);
    }

    [TestMethod]
    public void SameDestination_IsRecordedAsAlreadyPresent()
    {
        using var scope = new MigrationScope("AAML same");
        Write(scope.Source("Config", "settings.json"), "same");
        Write(scope.Destination("Config", "settings.json"), "same");

        var receipt = scope.Migrate();

        receipt.Status.Should().Be(DataRootMigrationStatus.Completed);
        receipt.Items.Single(item => item.Id == "settings").Outcome.Should().Be(DataRootMigrationOutcome.AlreadyPresent);
    }

    [TestMethod]
    public void ExistingDifferentDestination_WinsAndCompletedConflictDoesNotRewriteEndlessly()
    {
        using var scope = new MigrationScope("AAML conflict");
        var source = scope.Source("Config", "settings.json");
        var destination = scope.Destination("Config", "settings.json");
        Write(source, "former");
        Write(destination, "current");

        var first = scope.Migrate();
        var receiptBytes = File.ReadAllBytes(scope.ReceiptPath);
        var firstWrite = File.GetLastWriteTimeUtc(scope.ReceiptPath);
        Thread.Sleep(20);
        var second = scope.Migrate();

        first.Status.Should().Be(DataRootMigrationStatus.CompletedWithConflicts);
        second.Status.Should().Be(DataRootMigrationStatus.CompletedWithConflicts);
        second.Items.Single(item => item.Id == "settings").Outcome.Should().Be(DataRootMigrationOutcome.Conflict);
        File.ReadAllBytes(scope.ReceiptPath).Should().Equal(receiptBytes);
        File.GetLastWriteTimeUtc(scope.ReceiptPath).Should().Be(firstWrite);
        File.ReadAllText(destination).Should().Be("current");
        File.ReadAllText(source).Should().Be("former");
    }

    [TestMethod]
    public void CorruptPreviouslyCopiedDestination_IsReportedForExplicitRecoveryWithoutOverwrite()
    {
        using var scope = new MigrationScope("AAML corrupt destination");
        var source = scope.Source("Config", "settings.json");
        var destination = scope.Destination("Config", "settings.json");
        Write(source, "valid-source");
        scope.Migrate();
        File.WriteAllText(destination, "corrupt-destination");

        var receipt = scope.Migrate();

        receipt.Status.Should().Be(DataRootMigrationStatus.CompletedWithConflicts);
        receipt.Items.Single(item => item.Id == "settings").Should().Match<DataRootMigrationItem>(item =>
            item.Outcome == DataRootMigrationOutcome.Conflict && item.Message!.Contains("explicit recovery", StringComparison.Ordinal));
        File.ReadAllText(destination).Should().Be("corrupt-destination");
        File.ReadAllText(source).Should().Be("valid-source");
    }

    [TestMethod]
    public void CorruptReceipt_FailsClosedWithoutReplacingIt()
    {
        using var scope = new MigrationScope("AAML corrupt receipt");
        Directory.CreateDirectory(Path.GetDirectoryName(scope.ReceiptPath)!);
        File.WriteAllText(scope.ReceiptPath, "{not-json");

        Action act = () => scope.Migrate();

        act.Should().Throw<InvalidDataException>().WithMessage("*corrupt*");
        File.ReadAllText(scope.ReceiptPath).Should().Be("{not-json");
    }

    [TestMethod]
    public void SchemaOneReceipt_IsArchivedAsEvidenceAndMigrationRestartsTruthfully()
    {
        using var scope = new MigrationScope("AAML v1 receipt");
        Directory.CreateDirectory(Path.GetDirectoryName(scope.ReceiptPath)!);
        const string legacy = "{\"schemaVersion\":1,\"migrationId\":\"modern-data-root-v1\",\"status\":\"Completed\",\"items\":[]}";
        File.WriteAllText(scope.ReceiptPath, legacy);
        Write(scope.Source("Config", "settings.json"), "settings");

        var receipt = scope.Migrate();

        receipt.SchemaVersion.Should().Be(2);
        receipt.Status.Should().Be(DataRootMigrationStatus.Completed);
        File.ReadAllText(scope.LegacyReceiptPath).Should().Be(legacy);
        File.ReadAllText(scope.Destination("Config", "settings.json")).Should().Be("settings");
    }

    [TestMethod]
    [DataRow("XCOM2 Alternative Mod Launcher", "AAML")]
    [DataRow("xcom2-alternative-mod-launcher", "aaml")]
    public void Receipt_RecordsCanonicalRealFormerAndCurrentRootNames(string formerName, string currentName)
    {
        using var scope = new MigrationScope("AAML real root names", formerName, currentName);

        var receipt = scope.Migrate();

        Path.GetFileName(Path.GetDirectoryName(receipt.SourceRoot.ConfigurationDirectory)).Should().Be(formerName);
        Path.GetFileName(Path.GetDirectoryName(receipt.CurrentRoot.ConfigurationDirectory)).Should().Be(currentName);
    }

    public TestContext TestContext { get; set; } = null!;

    private static void WriteAllManifestSources(MigrationScope scope)
    {
        Write(scope.Source("Config", "settings.json"), "settings");
        Write(scope.Source("Config", "settings.json.bak"), "settings-backup");
        Write(scope.Source("Data", "Profiles", "profiles.json"), "profiles");
        Write(scope.Source("Data", "Profiles", "profiles.json.bak"), "profiles-backup");
        Write(scope.Source("Data", "ConfigurationSnapshots", "snapshots.json"), "snapshots");
        Write(scope.Source("Data", "ConfigurationSnapshots", "snapshots.json.bak"), "snapshots-backup");
        for (var index = 0; index <= 5; index++) Write(scope.Source("State", "Logs", index == 0 ? "aaml.log" : $"aaml.log.{index}"), $"log-{index}");
    }

    private static void Write(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private sealed class SimulatedInterruptionException : Exception;

    private sealed class MigrationScope : IDisposable
    {
        private readonly string root;

        public MigrationScope(string name, string formerName = "former", string currentName = "current")
        {
            root = Path.Combine(Path.GetTempPath(), name, Guid.NewGuid().ToString("N"));
            Former = new TestPaths(Path.Combine(root, formerName));
            Current = new TestPaths(Path.Combine(root, currentName));
        }

        public TestPaths Former { get; }
        public TestPaths Current { get; }
        public string ReceiptPath => Path.Combine(Current.StateDirectory, "Migrations", "modern-data-root-v1.json");
        public string LegacyReceiptPath => Path.Combine(Current.StateDirectory, "Migrations", "modern-data-root-v1.receipt-schema-v1.json");
        public string LockPath => Path.Combine(Current.StateDirectory, "Migrations", "modern-data-root-v1.lock");
        public string Source(params string[] parts) => Path.Combine([Former.Root, .. parts]);
        public string Destination(params string[] parts) => Path.Combine([Current.Root, .. parts]);
        public DataRootMigrationReceipt Migrate(CancellationToken cancellationToken = default, DataRootMigrationTestHooks? hooks = null) =>
            ModernDataRootMigrator.Migrate(Former, Current, cancellationToken, hooks);
        public JObject ReadReceipt() => JObject.Parse(File.ReadAllText(ReceiptPath));
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class TestPaths(string root) : IApplicationPaths
    {
        public string Root { get; } = root;
        public string ConfigurationDirectory { get; } = Path.Combine(root, "Config");
        public string DataDirectory { get; } = Path.Combine(root, "Data");
        public string StateDirectory { get; } = Path.Combine(root, "State");
        public string CacheDirectory { get; } = Path.Combine(root, "Cache");
        public string RuntimeDirectory { get; } = Path.Combine(root, "Runtime");
    }
}
