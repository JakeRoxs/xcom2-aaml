using AAML.Application.Configurations;
using AAML.Application.Ports;
using AAML.Domain.Mods;
using AAML.Infrastructure.Common.Configurations;
using AAML.Infrastructure.Common.Files;
using FluentAssertions;

namespace AAML.Infrastructure.Common.CharacterizationTests.Configurations;

[TestClass]
public sealed class JsonConfigurationSnapshotRepositoryTests
{
    [TestMethod]
    public async Task UpsertFindReplaceAndRemove_KeepPhysicalModIdentitiesSeparate()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML Snapshot Repository", Guid.NewGuid().ToString("N"));
        var repository = new JsonConfigurationSnapshotRepository(new TestPaths(root), new AtomicTextWriter());
        var first = Snapshot("C:\\Mods\\One", "first");
        var second = Snapshot("C:\\Mods\\Two", "second");
        try
        {
            (await repository.UpsertAsync(first, TestContext.CancellationToken)).IsSuccess.Should().BeTrue();
            (await repository.UpsertAsync(second, TestContext.CancellationToken)).IsSuccess.Should().BeTrue();
            (await repository.UpsertAsync(first with { Text = "updated" }, TestContext.CancellationToken)).IsSuccess.Should().BeTrue();

            (await repository.FindAsync(first.Id, TestContext.CancellationToken)).Value!.Text.Should().Be("updated");
            (await repository.FindAsync(second.Id, TestContext.CancellationToken)).Value!.Text.Should().Be("second");
            (await repository.RemoveAsync(first.Id, TestContext.CancellationToken)).IsSuccess.Should().BeTrue();
            (await repository.FindAsync(first.Id, TestContext.CancellationToken)).Value.Should().BeNull();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public TestContext TestContext { get; set; }
    private static SavedConfigurationSnapshot Snapshot(string root, string text) => new(new ConfigurationDocumentId(new ModKey(ModSource.Manual, root), "Config/XCom.ini"), text, new ConfigurationTextFormat(ConfigurationEncoding.Utf8, NewLineStyle.Lf));
    private sealed class TestPaths(string root) : IApplicationPaths
    {
        public string ConfigurationDirectory { get; } = Path.Combine(root, "Config");
        public string DataDirectory { get; } = Path.Combine(root, "Data");
        public string StateDirectory { get; } = Path.Combine(root, "State");
        public string CacheDirectory { get; } = Path.Combine(root, "Cache");
        public string RuntimeDirectory { get; } = Path.Combine(root, "Runtime");
    }
}
