using AAML.Domain.Games;
using AAML.Domain.Mods;
using AAML.Infrastructure.Common.Configurations;
using FluentAssertions;

namespace AAML.Infrastructure.Common.CharacterizationTests.Configurations;

[TestClass]
public sealed class FilesystemConfigurationDocumentCatalogTests
{
    [TestMethod]
    public async Task VariantFiltering_ReturnsOnlyRealRecursiveIniFilesInStableOrder()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML Configuration Catalog", Guid.NewGuid().ToString("N"));
        var vanillaRoot = Path.Combine(root, "Vanilla");
        var wotcRoot = Path.Combine(root, "WotC");
        try
        {
            Directory.CreateDirectory(Path.Combine(vanillaRoot, "Config", "Nested"));
            Directory.CreateDirectory(Path.Combine(wotcRoot, "Config"));
            await File.WriteAllTextAsync(Path.Combine(vanillaRoot, "Config", "Nested", "XComB.ini"), "B", TestContext.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(vanillaRoot, "Config", "XComA.ini"), "A", TestContext.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(vanillaRoot, "Config", "ignored.txt"), "ignored", TestContext.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(wotcRoot, "Config", "XComWotC.ini"), "W", TestContext.CancellationToken);
            var installations = new[] { Mod(wotcRoot, "WotC", true), Mod(vanillaRoot, "Vanilla", false) };
            var catalog = new FilesystemConfigurationDocumentCatalog();

            var vanilla = await catalog.ListAsync(installations, GameVariant.XCom2, TestContext.CancellationToken);
            var wotc = await catalog.ListAsync(installations, GameVariant.XCom2WarOfTheChosen, TestContext.CancellationToken);

            vanilla.Value!.Select(item => item.RelativePath).Should().Equal("Config/Nested/XComB.ini", "Config/XComA.ini");
            wotc.Value!.Should().HaveCount(3).And.Contain(item => item.ModName == "WotC" && item.RelativePath == "Config/XComWotC.ini");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public TestContext TestContext { get; set; }
    private static ModInstallation Mod(string root, string name, bool requiresWotc) => new(new ModKey(ModSource.Manual, root), new PackageId(name), name, null, requiresWotc, DescriptorState.Enabled, null);
}
