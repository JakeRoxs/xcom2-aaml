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
