using AAML.Infrastructure.Common.Settings;
using FluentAssertions;

namespace AAML.Infrastructure.Common.CharacterizationTests.Settings;

[TestClass]
public sealed class LegacyLaunchArgumentSuggestionRepositoryTests
{
    [TestMethod]
    public async Task MissingFile_ReturnsNoDocumentOrDiagnostic()
    {
        var repository = new LegacyLaunchArgumentSuggestionRepository(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.json"));

        var result = await repository.LoadAsync(TestContext.CancellationToken);

        result.Document.Should().BeNull();
        result.Diagnostics.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ValidV1Report_ReadsSourceAndStringSuggestionsWhileDiagnosingMalformedEntries()
    {
        var path = await WriteAsync("""{"schemaVersion":1,"sourceSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","sourcePreserved":true,"quickToggleArguments":["-custom",42,null]}""");
        try
        {
            var result = await new LegacyLaunchArgumentSuggestionRepository(path).LoadAsync(TestContext.CancellationToken);

            result.Document.Should().NotBeNull();
            result.Document!.SchemaVersion.Should().Be(1);
            result.Document.SourcePreserved.Should().BeTrue();
            result.Document.Arguments.Should().Equal("-custom");
            result.Diagnostics.Should().HaveCount(2).And.OnlyContain(diagnostic => diagnostic.Code == "launch_presets.malformed_report_entry");
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [TestMethod]
    public async Task CorruptJson_ReturnsDiagnosticInsteadOfThrowing()
    {
        var path = await WriteAsync("{");
        try
        {
            var result = await new LegacyLaunchArgumentSuggestionRepository(path).LoadAsync(TestContext.CancellationToken);

            result.Document.Should().BeNull();
            result.Diagnostics.Should().ContainSingle(diagnostic => diagnostic.Code == "launch_presets.report_read_failed");
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    public TestContext TestContext { get; set; }

    private async Task<string> WriteAsync(string json)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"aaml-preset-report-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "legacy-migration-v1.json");
        await File.WriteAllTextAsync(path, json, TestContext.CancellationToken);
        return path;
    }
}
