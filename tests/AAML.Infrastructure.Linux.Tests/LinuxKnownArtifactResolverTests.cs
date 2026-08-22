using AAML.Infrastructure.Linux.Paths;
using FluentAssertions;

namespace AAML.Infrastructure.Linux.Tests;

[TestClass]
public sealed class LinuxKnownArtifactResolverTests
{
    [TestMethod]
    public void ExactPathWinsEvenWhenAnotherCaseVariantExists()
    {
        RequireLinux();
        var root = TemporaryRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Win64"));
            Directory.CreateDirectory(Path.Combine(root, "win64"));

            var result = Resolver().ResolveExistingDirectory(root, "Win64");

            result.IsSuccess.Should().BeTrue(result.Error?.Message);
            result.Value!.Path.Should().Be(Path.Combine(root, "Win64"));
            result.Value.CaseFallbacks.Should().BeEmpty();
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void UniqueFallbackResolvesEveryKnownComponentAndRecordsActualCasing()
    {
        RequireLinux();
        var root = TemporaryRoot();
        try
        {
            var target = Path.Combine(root, "binaries", "WIN64", "XCOM2.EXE");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, []);

            var result = Resolver().ResolveExistingFile(root, "Binaries", "Win64", "XCom2.exe");

            result.IsSuccess.Should().BeTrue(result.Error?.Message);
            result.Value!.Path.Should().Be(target);
            result.Value.CaseFallbacks.Should().HaveCount(3);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MultipleFallbackMatchesAreRejectedWithCandidateDiagnostics()
    {
        RequireLinux();
        var root = TemporaryRoot();
        try
        {
            File.WriteAllBytes(Path.Combine(root, "XCOM2.EXE"), []);
            File.WriteAllBytes(Path.Combine(root, "xcom2.exe"), []);

            var result = Resolver().ResolveExistingFile(root, "XCom2.exe");

            result.Error!.Code.Should().Be("path.known_artifact_case_ambiguous");
            result.Error.Message.Should().Contain("XCOM2.EXE").And.Contain("xcom2.exe");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MissingGeneratedTailUsesExpectedNamesAfterResolvedExistingParents()
    {
        RequireLinux();
        var root = TemporaryRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "documents"));

            var result = Resolver().ResolveDirectoryExistingOrExpected(root, "Documents", "My Games", "XCOM2", "XComGame", "Config");

            result.IsSuccess.Should().BeTrue(result.Error?.Message);
            result.Value!.Exists.Should().BeFalse();
            result.Value.Path.Should().Be(Path.Combine(root, "documents", "My Games", "XCOM2", "XComGame", "Config"));
            result.Value.CaseFallbacks.Should().ContainSingle();
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void SymlinkedArtifactOutsideQualifiedRootIsRejected()
    {
        RequireLinux();
        var root = TemporaryRoot();
        var outside = TemporaryRoot();
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "Binaries"), outside);

            var result = Resolver().ResolveExistingDirectory(root, "Binaries");

            result.Error!.Code.Should().Be("path.known_artifact_outside_root");
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(outside, true);
        }
    }

    [TestMethod]
    public void SymlinkedExistingParentCannotEscapeBeforeMissingGeneratedTail()
    {
        RequireLinux();
        var root = TemporaryRoot();
        var outside = TemporaryRoot();
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "Documents"), outside);

            var result = Resolver().ResolveDirectoryExistingOrExpected(root, "Documents", "My Games", "XCOM2");

            result.Error!.Code.Should().Be("path.known_artifact_outside_root");
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(outside, true);
        }
    }

    [TestMethod]
    public void ExpectedFileRejectsDirectoryAndEmptyComponentList()
    {
        RequireLinux();
        var root = TemporaryRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "XCom2.exe"));

            Resolver().ResolveExistingFile(root, "XCom2.exe").Error!.Code.Should().Be("path.known_artifact_type_mismatch");
            Resolver().ResolveExistingDirectory(root).Error!.Code.Should().Be("path.known_artifact_component_invalid");
        }
        finally { Directory.Delete(root, true); }
    }

    public TestContext TestContext { get; set; }

    private static LinuxKnownArtifactResolver Resolver() => new(new LinuxPhysicalPathResolver());
    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "aaml-known-artifacts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("Known artifact casing requires a case-sensitive Linux filesystem.");
    }
}
