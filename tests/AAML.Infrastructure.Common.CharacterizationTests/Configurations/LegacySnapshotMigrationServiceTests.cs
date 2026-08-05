using AAML.Application.Common;
using AAML.Application.Configurations;
using AAML.Application.Ports;
using AAML.Infrastructure.Common.Configurations;
using AAML.Infrastructure.Common.Files;
using FluentAssertions;

namespace AAML.Infrastructure.Common.CharacterizationTests.Configurations;

[TestClass]
public sealed class LegacySnapshotMigrationServiceTests
{
    [TestMethod]
    public async Task CurrentFixture_PreviewsAndImportsEmbeddedSnapshotWithoutChangingSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML Legacy Snapshots", Guid.NewGuid().ToString("N"));
        var repository = new JsonConfigurationSnapshotRepository(new TestPaths(root), new AtomicTextWriter());
        var service = new LegacySnapshotMigrationService(repository, new TestPathSemantics());
        var source = CompatibilityFixture.Read("settings", "current-xcom2.json");
        try
        {
            var preview = await service.PreviewAsync("legacy-settings.json", source, TestContext.CancellationToken);
            preview.IsSuccess.Should().BeTrue();
            preview.Value!.Items.Should().ContainSingle(item => item.Action == LegacySnapshotAction.Import);
            preview.Value.Items[0].Snapshot!.Text.Should().Be("[Synthetic.Section]\r\nValue=Fixture\r\n");
            preview.Value.Items[0].Snapshot!.Format.NewLines.Should().Be(NewLineStyle.CrLf);

            (await service.ApplyAsync(preview.Value, TestContext.CancellationToken)).IsSuccess.Should().BeTrue();
            var repeated = await service.PreviewAsync("legacy-settings.json", source, TestContext.CancellationToken);
            repeated.Value!.Items.Should().ContainSingle(item => item.Action == LegacySnapshotAction.AlreadyImported);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public TestContext TestContext { get; set; }
    private sealed class TestPaths(string root) : IApplicationPaths
    {
        public string ConfigurationDirectory => root;
        public string DataDirectory => root;
        public string StateDirectory => root;
        public string CacheDirectory => root;
        public string RuntimeDirectory => root;
    }
    private sealed class TestPathSemantics : IPathSemantics
    {
        public Result<string> NormalizeIdentity(string path) => string.IsNullOrWhiteSpace(path) ? Result<string>.Failure(new Error("path", "invalid", ErrorKind.InvalidData)) : Result<string>.Success(path.Replace('/', '\\').TrimEnd('\\'));
        public bool AreEqual(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        public Result<bool> IsContainedBy(string candidate, string parent) => Result<bool>.Success(true);
    }
}
