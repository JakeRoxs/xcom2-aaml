using AAML.Infrastructure.Windows.Paths;
using FluentAssertions;

namespace AAML.Infrastructure.Windows.Tests;

[TestClass]
public sealed class WindowsApplicationPathsTests
{
    [TestMethod]
    public void Paths_AreExplicitDistinctAndIndependentOfCurrentDirectory()
    {
        var paths = new WindowsApplicationPaths("C:\\Users\\Zoë 李\\AppData\\Local");

        paths.ConfigurationDirectory.Should().EndWith("AAML\\Config");
        paths.DataDirectory.Should().EndWith("AAML\\Data");
        paths.StateDirectory.Should().EndWith("AAML\\State");
        paths.CacheDirectory.Should().EndWith("AAML\\Cache");
        paths.RuntimeDirectory.Should().EndWith("AAML\\Runtime");
        new[] { paths.ConfigurationDirectory, paths.DataDirectory, paths.StateDirectory, paths.CacheDirectory, paths.RuntimeDirectory }.Should().OnlyHaveUniqueItems();
        paths.ConfigurationDirectory.Should().NotContain("/");
    }
}
