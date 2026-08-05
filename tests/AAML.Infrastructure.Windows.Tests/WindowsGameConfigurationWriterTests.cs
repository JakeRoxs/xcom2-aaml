using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Domain.Games;
using AAML.Domain.Launching;
using AAML.Domain.Mods;
using AAML.Infrastructure.Windows.Launching;
using FluentAssertions;

namespace AAML.Infrastructure.Windows.Tests;

[TestClass]
public sealed class WindowsGameConfigurationWriterTests
{
    [TestMethod]
    [DataRow(GameVariant.XCom2, "XCOM2")]
    [DataRow(GameVariant.XCom2WarOfTheChosen, "XCOM2 War of the Chosen")]
    [DataRow(GameVariant.ChimeraSquad, "XCOM Chimera Squad")]
    public async Task Variant_WritesExactUserPathsAndPreservesUnrelatedIniValues(GameVariant variant, string gameFolder)
    {
        var documents = Path.Combine(Path.GetTempPath(), "AAML Documents Ω", Guid.NewGuid().ToString("N"));
        var config = Path.Combine(documents, "My Games", gameFolder, "XComGame", "Config");
        try
        {
            Directory.CreateDirectory(config);
            await File.WriteAllTextAsync(Path.Combine(config, "XComModOptions.ini"), "[Other]\nKeep=Yes\n[Engine.XComModOptions]\n+ActiveMods=OldAdded\n!ActiveMods=ClearArray\nActiveMods=Old\n", TestContext.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(config, "XComEngine.ini"), "[Engine.DownloadableContentEnumerator]\n+ModRootDirs=OldAddedRoot\n-ModRootDirs=OldRemovedRoot\n!ModRootDirs=ClearArray\nModRootDirs=OldRoot\n[Other]\nKeep=Yes\n", TestContext.CancellationToken);
            var atomic = new RecordingAtomicWriter();
            var writer = new WindowsGameConfigurationWriter(atomic, documents);
            var mods = new[]
            {
                new GameLaunchMod(new ModKey(ModSource.Manual, "C:\\Mods\\Two"), new PackageId("Second"), 2, false),
                new GameLaunchMod(new ModKey(ModSource.Manual, "C:\\Mods\\One"), new PackageId("First"), 1, false)
            };
            var request = new GameLaunchRequest(variant, "C:\\Games\\XCOM 2", ["C:\\Steam\\steamapps\\workshop\\content\\268500", "D:\\Manual Mods"], mods, []);

            var result = await writer.ApplyAsync(request, TestContext.CancellationToken);

            result.Value!.WrittenFiles.Should().Equal(Path.Combine(config, "XComModOptions.ini"), Path.Combine(config, "XComEngine.ini"));
            result.Value.ActivePackageIds.Select(id => id.Value).Should().Equal("First", "Second");
            result.Value.ModRootLocations.Should().Equal("C:\\Steam\\steamapps\\workshop\\content\\268500", "D:\\Manual Mods");
            atomic.Contents[Path.Combine(config, "XComModOptions.ini")].Should().Contain("Keep=Yes").And.Contain("ActiveMods=First\nActiveMods=Second").And.NotContain("OldAdded").And.NotContain("ClearArray").And.NotContain("ActiveMods=Old");
            atomic.Contents[Path.Combine(config, "XComEngine.ini")].Should().Contain("Keep=Yes").And.Contain("ModRootDirs=C:\\Steam\\steamapps\\workshop\\content\\268500\\\nModRootDirs=D:\\Manual Mods\\").And.NotContain("OldAddedRoot").And.NotContain("OldRemovedRoot").And.NotContain("ClearArray").And.NotContain("ModRootDirs=OldRoot");
        }
        finally { if (Directory.Exists(documents)) Directory.Delete(documents, true); }
    }

    public TestContext TestContext { get; set; }

    private sealed class RecordingAtomicWriter : IAtomicTextWriter
    {
        public Dictionary<string, string> Contents { get; } = new(StringComparer.Ordinal);
        public Task<Result> WriteAsync(string path, string content, CancellationToken cancellationToken) { Contents[path] = content; return Task.FromResult(Result.Success()); }
    }
}
