using AAML.Application.Configurations;
using AAML.Domain.Games;
using AAML.Infrastructure.Common.Files;
using AAML.Infrastructure.Windows.Launching;
using FluentAssertions;

namespace AAML.Infrastructure.Windows.Tests;

[TestClass]
public sealed class WindowsLegacyGameConfigurationSourceTests
{
    [TestMethod]
    public async Task ModRoots_UseExactVariantPathsResolveRelativeAndClassifyWithoutWritingSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML Root Preview", Guid.NewGuid().ToString("N"));
        var documents = Path.Combine(root, "Documents");
        try
        {
            foreach (var variant in new[] { GameVariant.XCom2, GameVariant.XCom2WarOfTheChosen, GameVariant.ChimeraSquad })
            {
                var installation = Path.Combine(root, variant.ToString());
                var binary = Path.Combine(new[] { installation }.Concat(GameModRootPolicy.BinaryDirectoryComponents(variant)).ToArray());
                var relativeRoot = Path.Combine(binary, "Relative Mods");
                var absoluteRoot = Path.Combine(root, "Absolute Mods", variant.ToString());
                Directory.CreateDirectory(relativeRoot); Directory.CreateDirectory(absoluteRoot);
                var config = Path.Combine(documents, "My Games", GameModRootPolicy.WindowsDocumentsFolder(variant), "XComGame", "Config");
                Directory.CreateDirectory(config);
                var engine = Path.Combine(config, "XComEngine.ini");
                var removedRoot = Path.Combine(root, "Removed");
                var original = $"[Engine.DownloadableContentEnumerator]\r\n!ModRootDirs=ClearArray\r\n+ModRootDirs={removedRoot}\r\n-ModRootDirs=\"{removedRoot}\"\r\n+ModRootDirs=\"Relative Mods\\\"\r\nModRootDirs={absoluteRoot}\r\n.ModRootDirs={absoluteRoot}\r\nModRootDirs={Path.Combine(root, "Missing")}\r\nModRootDirs=..\\..\\..\\..\\..\\Outside\r\nModRootDirs=\"unterminated\r\nModRootDirs\r\n[Other]\r\nModRootDirs=ignored\r\n";
                await File.WriteAllTextAsync(engine, original, TestContext.CancellationToken);
                var service = new WindowsLegacyGameConfigurationSource(new AtomicTextWriter(), documents);

                var preview = await service.ReadModRootsAsync(variant, installation, [absoluteRoot], TestContext.CancellationToken);

                preview.IsSuccess.Should().BeTrue(preview.Error?.Message);
                preview.Value!.SourcePath.Should().Be(engine);
                preview.Value.Rows.Select(row => row.Resolution).Should().Equal(
                    ExistingModRootResolution.Valid,
                    ExistingModRootResolution.AlreadyConfigured,
                    ExistingModRootResolution.Duplicate,
                    ExistingModRootResolution.Missing,
                    ExistingModRootResolution.OutsideRoot,
                    ExistingModRootResolution.Malformed,
                    ExistingModRootResolution.Malformed);
                preview.Value.Rows[0].ResolvedPath.Should().Be(Path.GetFullPath(relativeRoot));
                (await File.ReadAllTextAsync(engine, TestContext.CancellationToken)).Should().Be(original);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ModRoots_ClassifyReparseDirectoryWhenSymbolicLinksAreAvailable()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML Root Link", Guid.NewGuid().ToString("N"));
        var documents = Path.Combine(root, "Documents"); var installation = Path.Combine(root, "Game");
        var target = Path.Combine(root, "Target"); var link = Path.Combine(root, "Link");
        try
        {
            Directory.CreateDirectory(target); Directory.CreateDirectory(installation);
            try { Directory.CreateSymbolicLink(link, target); }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException) { Assert.Inconclusive($"Symbolic links unavailable: {exception.Message}"); return; }
            var config = Path.Combine(documents, "My Games", "XCOM2", "XComGame", "Config"); Directory.CreateDirectory(config);
            await File.WriteAllTextAsync(Path.Combine(config, "XComEngine.ini"), $"[Engine.DownloadableContentEnumerator]\nModRootDirs={link}\n", TestContext.CancellationToken);

            var preview = await new WindowsLegacyGameConfigurationSource(new AtomicTextWriter(), documents).ReadModRootsAsync(GameVariant.XCom2, installation, [], TestContext.CancellationToken);

            preview.Value!.Rows.Single().Resolution.Should().Be(ExistingModRootResolution.Reparse);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ActiveMods_ReadsGeneratedBeforeDefaultAndCleanupIsPreviewedBackupSafeAndIdempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML Legacy Config", Guid.NewGuid().ToString("N"));
        var documents = Path.Combine(root, "Documents");
        var installation = Path.Combine(root, "Game");
        var userConfig = Path.Combine(documents, "My Games", "XCOM2 War of the Chosen", "XComGame", "Config");
        var defaultConfig = Path.Combine(installation, "XCom2-WarOfTheChosen", "XComGame", "Config");
        Directory.CreateDirectory(userConfig); Directory.CreateDirectory(defaultConfig);
        var generated = Path.Combine(userConfig, "XComModOptions.ini");
        var defaults = Path.Combine(defaultConfig, "DefaultModOptions.ini");
        var engine = Path.Combine(userConfig, "XComEngine.ini");
        await File.WriteAllTextAsync(generated, "[Engine.XComModOptions]\r\nActiveMods=Generated\r\n");
        await File.WriteAllTextAsync(defaults, "[Engine.XComModOptions]\r\nActiveMods=Default\r\n");
        var original = "[Engine.Engine]\r\n; +ModClassOverrides=comment\r\n+ModClassOverrides=(BaseGameClass=Old)\r\nOther=Keep\r\n[Other]\r\nModClassOverrides=Keep\r\n";
        await File.WriteAllTextAsync(engine, original);
        var service = new WindowsLegacyGameConfigurationSource(new AtomicTextWriter(), documents);
        try
        {
            var sources = await service.ReadActiveModsAsync(GameVariant.XCom2WarOfTheChosen, installation, TestContext.CancellationToken);
            sources.Value!.Select(source => source.IsGenerated).Should().Equal(true, false);

            var preview = await service.PreviewOverrideCleanupAsync(GameVariant.XCom2WarOfTheChosen, TestContext.CancellationToken);
            preview.Value!.RemovedRows.Should().Be(1);
            (await service.ApplyOverrideCleanupAsync(preview.Value, TestContext.CancellationToken)).IsSuccess.Should().BeTrue();
            (await File.ReadAllTextAsync(engine)).Should().Contain("Other=Keep").And.Contain("[Other]\r\nModClassOverrides=Keep").And.Contain("; +ModClassOverrides=comment").And.NotContain("+ModClassOverrides=(BaseGameClass=Old)");
            (await File.ReadAllTextAsync(engine + ".bak")).Should().Be(original);
            (await service.PreviewOverrideCleanupAsync(GameVariant.XCom2WarOfTheChosen, TestContext.CancellationToken)).Value!.RemovedRows.Should().Be(0);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public TestContext TestContext { get; set; }
}
