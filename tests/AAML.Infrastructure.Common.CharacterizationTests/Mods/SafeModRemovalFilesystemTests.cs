using AAML.Domain.Mods;
using AAML.Infrastructure.Common.Mods;
using FluentAssertions;

namespace AAML.Infrastructure.Common.CharacterizationTests.Mods;

[TestClass]
public sealed class SafeModRemovalFilesystemTests
{
    [TestMethod]
    public async Task PreviewThenConfirm_DeletesOnlyContainedManualChildAndRejectsTokenReuse()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML Removal", Guid.NewGuid().ToString("N")); var mod = Path.Combine(root, "Mod"); Directory.CreateDirectory(mod); await File.WriteAllTextAsync(Path.Combine(mod, "file.txt"), "fixture", TestContext.CancellationToken);
        try
        {
            var service = new SafeModRemovalFilesystem(new HostPaths()); var key = new ModKey(ModSource.Manual, mod);
            var preview = await service.PreviewAsync(key, [root], TestContext.CancellationToken);
            preview.Value!.FileCount.Should().Be(1); preview.Value.TotalBytes.Should().Be(7);
            (await service.DeleteConfirmedAsync(preview.Value, TestContext.CancellationToken)).IsSuccess.Should().BeTrue(); Directory.Exists(mod).Should().BeFalse();
            (await service.DeleteConfirmedAsync(preview.Value, TestContext.CancellationToken)).Error!.Code.Should().Be("removal.confirmation_invalid");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task Preview_RejectsWorkshopAndConfiguredRootItself()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML Removal", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { var service = new SafeModRemovalFilesystem(new HostPaths()); (await service.PreviewAsync(new ModKey(ModSource.SteamWorkshop, root), [root], TestContext.CancellationToken)).Error!.Code.Should().Be("removal.workshop_forbidden"); (await service.PreviewAsync(new ModKey(ModSource.Manual, root), [root], TestContext.CancellationToken)).Error!.Code.Should().Be("removal.outside_roots"); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    public TestContext TestContext { get; set; }
    private sealed class HostPaths : AAML.Application.Ports.IPathSemantics { public AAML.Application.Common.Result<string> NormalizeIdentity(string path) => AAML.Application.Common.Result<string>.Success(Path.GetFullPath(path)); public bool AreEqual(string a, string b) => Path.GetFullPath(a).Equals(Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal); public AAML.Application.Common.Result<bool> IsContainedBy(string candidate, string parent) => AAML.Application.Common.Result<bool>.Success(Path.GetRelativePath(parent, candidate) is var relative && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar)); }
}
