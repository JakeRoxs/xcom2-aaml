using AAML.Application.Common;
using AAML.Application.Mods.Cleanup;
using AAML.Application.Ports;
using AAML.Domain.Mods;
using AAML.Infrastructure.Common.Mods;
using FluentAssertions;

namespace AAML.Infrastructure.Common.CharacterizationTests.Mods;

[TestClass]
public sealed class SafeModCleanupServiceTests
{
    [TestMethod]
    public async Task PreviewIsNonMutatingAndConfirmationIsRevisionBoundSingleUse()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML cleanup", Guid.NewGuid().ToString("N")); var modPath = Path.Combine(root, "Mod"); var source = Path.Combine(modPath, "src", "XComGame"); Directory.CreateDirectory(source); await File.WriteAllTextAsync(Path.Combine(source, "Class.uc"), "original");
        var service = new SafeModCleanupService(new HostPaths(), TimeProvider.System); var mod = new ModInstallation(new(ModSource.Manual, modPath), new("Package"), "Mod", null, false, DescriptorState.Enabled, null);
        try
        {
            var preview = await service.PreviewAsync(new([mod], SourceCleanupPolicy.XComGameOnly, ShaderCleanupPolicy.None, false, [root]), TestContext.CancellationToken);
            Directory.Exists(source).Should().BeTrue(); preview.Value!.Items.Single().Disposition.Should().Be(CleanupDisposition.Ready);
            var executed = await service.ExecuteAsync(preview.Value, TestContext.CancellationToken); executed.Value!.Items.Single().Outcome.Should().Be(CleanupItemOutcome.Deleted); Directory.Exists(source).Should().BeFalse();
            (await service.ExecuteAsync(preview.Value, TestContext.CancellationToken)).Error!.Code.Should().Be("cleanup.confirmation_invalid");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    public TestContext TestContext { get; set; }
    private sealed class HostPaths : IPathSemantics
    {
        public Result<string> NormalizeIdentity(string path) => Result<string>.Success(Path.GetFullPath(path));
        public bool AreEqual(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        public Result<bool> IsContainedBy(string candidate, string parent) { var relative = Path.GetRelativePath(parent, candidate); return Result<bool>.Success(relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar)); }
    }
}
