using System.Text;
using AAML.Infrastructure.Common.Compatibility.Files;
using FluentAssertions;

namespace AAML.Infrastructure.Common.CharacterizationTests.Files;

[TestClass]
public sealed class LegacyAtomicTextWriterTests
{
    private string directory = null!;

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(Path.GetTempPath(), "AAML.Characterization", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(directory, true);

    [TestMethod]
    public void NewFile_IsUtf8WithoutBomAndLeavesNoArtifacts()
    {
        var path = Path.Combine(directory, "settings.json");

        LegacyAtomicTextWriter.Write(path, "Synthetic Ω");

        File.ReadAllBytes(path).Should().Equal(Encoding.UTF8.GetBytes("Synthetic Ω"));
        File.Exists(path + ".bak").Should().BeFalse();
        File.Exists(path + ".tmp").Should().BeFalse();
    }

    [TestMethod]
    public void SequentialWrites_RotateOneGenerationBackup()
    {
        var path = Path.Combine(directory, "settings.json");
        LegacyAtomicTextWriter.Write(path, "A");
        LegacyAtomicTextWriter.Write(path, "B");
        LegacyAtomicTextWriter.Write(path, "C");

        File.ReadAllText(path).Should().Be("C");
        File.ReadAllText(path + ".bak").Should().Be("B");
        File.Exists(path + ".tmp").Should().BeFalse();
    }
}
