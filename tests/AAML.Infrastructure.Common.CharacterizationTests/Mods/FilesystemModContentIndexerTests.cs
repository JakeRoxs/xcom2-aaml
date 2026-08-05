using AAML.Domain.Mods;
using AAML.Infrastructure.Common.Mods;
using FluentAssertions;

namespace AAML.Infrastructure.Common.CharacterizationTests.Mods;

[TestClass]
public sealed class FilesystemModContentIndexerTests
{
    [TestMethod]
    public async Task Index_ExtractsFilesClassOverridesAndListenersDeterministically()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML Conflict Fixture", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Config"));
            Directory.CreateDirectory(Path.Combine(root, "Src", "Package", "Classes"));
            Directory.CreateDirectory(Path.Combine(root, "Cooked"));
            await File.WriteAllTextAsync(Path.Combine(root, "Config", "XComEngine.ini"), "[Engine.Engine]\n+ ModClassOverrides = ( BaseGameClass = \"FixtureBase\", ModClass = \"FixtureReplacement\" )\n", TestContext.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "Src", "Package", "Classes", "FixtureListener.uc"), "defaultproperties\n{\n ScreenClass = class'FixtureScreen'\n}\n", TestContext.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "Cooked", "Shared.upk"), "fixture", TestContext.CancellationToken);
            var installation = new ModInstallation(new ModKey(ModSource.Manual, root), new PackageId("Fixture"), "Fixture", null, true, DescriptorState.Enabled, null);

            var result = await new FilesystemModContentIndexer().IndexAsync(installation, TestContext.CancellationToken);

            result.Value!.Files.Select(file => file.RelativePath).Should().Equal("Config/XComEngine.ini", "Cooked/Shared.upk", "Src/Package/Classes/FixtureListener.uc");
            result.Value.Overrides.Should().ContainSingle(fact => fact.Kind == AAML.Application.Mods.Conflicts.ModOverrideKind.Class && fact.BaseClass == "FixtureBase" && fact.ReplacementClass == "FixtureReplacement" && fact.LineNumber == 2);
            result.Value.Overrides.Should().ContainSingle(fact => fact.Kind == AAML.Application.Mods.Conflicts.ModOverrideKind.UiScreenListener && fact.BaseClass == "FixtureScreen" && fact.ReplacementClass == "FixtureListener" && fact.LineNumber == 3);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public TestContext TestContext { get; set; }
}
