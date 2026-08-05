using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Infrastructure.Windows.Paths;
using FluentAssertions;

namespace AAML.Infrastructure.Windows.Tests;

[TestClass]
public sealed class KnownGameArtifactResolverTests
{
    [TestMethod]
    public async Task Resolver_FindsKnownComponentsWithActualCasing()
    {
        var root = CreateRoot();
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, "binaries", "WIN64"));
            var executable = Path.Combine(directory.FullName, "XCom2.exe");
            await File.WriteAllTextAsync(executable, string.Empty);
            var resolver = new KnownGameArtifactResolver();

            var result = await resolver.ResolveAsync(
                new KnownGameArtifactRequest(root, ["Binaries", "Win64", "XCom2.exe"], ArtifactKind.File),
                TestContext.CancellationToken);

            result.IsSuccess.Should().BeTrue();
            result.Value!.Path.Should().Be(executable);
            result.Value.ActualComponents.Should().Equal("binaries", "WIN64", "XCom2.exe");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task Resolver_ReportsMissingAndWrongType()
    {
        var root = CreateRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Config"));
            var resolver = new KnownGameArtifactResolver();

            var missing = await resolver.ResolveAsync(new KnownGameArtifactRequest(root, ["Missing"], ArtifactKind.Directory), TestContext.CancellationToken);
            var wrongType = await resolver.ResolveAsync(new KnownGameArtifactRequest(root, ["Config"], ArtifactKind.File), TestContext.CancellationToken);

            missing.Error!.Kind.Should().Be(ErrorKind.NotFound);
            wrongType.Error!.Code.Should().Be("artifact.type_mismatch");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    public TestContext TestContext { get; set; }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML.Artifacts", Guid.NewGuid().ToString("N"), "Unicode Ω Folder");
        Directory.CreateDirectory(root);
        return root;
    }
}
