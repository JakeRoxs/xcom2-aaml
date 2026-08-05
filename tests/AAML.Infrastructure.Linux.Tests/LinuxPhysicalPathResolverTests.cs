using AAML.Infrastructure.Linux.Paths;
using FluentAssertions;

namespace AAML.Infrastructure.Linux.Tests;

[TestClass]
public sealed class LinuxPhysicalPathResolverTests
{
    [TestMethod]
    public void SymlinkAlias_ResolvesToSamePhysicalIdentityOnLinux()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("Physical symlink identity requires Linux.");
        var root = Path.Combine(Path.GetTempPath(), "AAML.LinuxPaths", Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "Steam Library");
        var alias = Path.Combine(root, "steam-root");
        try
        {
            Directory.CreateDirectory(target);
            Directory.CreateSymbolicLink(alias, target);
            var resolver = new LinuxPhysicalPathResolver();

            var physicalTarget = resolver.ResolveExisting(target);
            var physicalAlias = resolver.ResolveExisting(alias);

            physicalAlias.Value.Should().Be(physicalTarget.Value);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
