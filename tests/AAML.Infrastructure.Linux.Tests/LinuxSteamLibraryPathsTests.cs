using AAML.Infrastructure.Linux.Paths;
using FluentAssertions;

namespace AAML.Infrastructure.Linux.Tests;

[TestClass]
public sealed class LinuxSteamLibraryPathsTests
{
    [TestMethod]
    public void MultipleLibraries_NormalizeDeduplicateAndPreserveCaseUnicodeAndSpaces()
    {
        var result = LinuxSteamLibraryPaths.Normalize([
            "/home/Zoë 李/.local/share/Steam/steamapps/..",
            "/home/Zoë 李/.local/share/Steam",
            "/mnt/Games SSD/SteamLibrary/",
            "/mnt/Games SSD/steamlibrary/"]);

        result.Value.Should().Equal(
            "/home/Zoë 李/.local/share/Steam",
            "/mnt/Games SSD/SteamLibrary",
            "/mnt/Games SSD/steamlibrary");
    }
}
