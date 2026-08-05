using AAML.Infrastructure.Linux.Paths;
using FluentAssertions;

namespace AAML.Infrastructure.Linux.Tests;

[TestClass]
public sealed class LinuxApplicationPathsTests
{
    [TestMethod]
    public void AbsoluteXdgOverrides_AreHonoredAndDistinct()
    {
        var paths = new LinuxApplicationPaths(new LinuxApplicationPathOptions(
            "/home/Zoë 李", "/xdg/config", "/xdg/data", "/xdg/state", "/xdg/cache", "/run/user/1000", "/fallback/runtime"));

        paths.ConfigurationDirectory.Should().Be("/xdg/config/aaml");
        paths.DataDirectory.Should().Be("/xdg/data/aaml");
        paths.StateDirectory.Should().Be("/xdg/state/aaml");
        paths.CacheDirectory.Should().Be("/xdg/cache/aaml");
        paths.RuntimeDirectory.Should().Be("/run/user/1000/aaml");
        paths.UsesRuntimeFallback.Should().BeFalse();
        new[] { paths.ConfigurationDirectory, paths.DataDirectory, paths.StateDirectory, paths.CacheDirectory, paths.RuntimeDirectory }.Should().OnlyHaveUniqueItems();
    }

    [TestMethod]
    public void MissingEmptyAndRelativeOverrides_UseSpecifiedDefaultsAndRuntimeFallback()
    {
        var paths = new LinuxApplicationPaths(new LinuxApplicationPathOptions(
            "/home/Zoë 李", null, string.Empty, "relative/state", null, "relative/runtime", "/run/aaml-fallback"));

        paths.ConfigurationDirectory.Should().Be("/home/Zoë 李/.config/aaml");
        paths.DataDirectory.Should().Be("/home/Zoë 李/.local/share/aaml");
        paths.StateDirectory.Should().Be("/home/Zoë 李/.local/state/aaml");
        paths.CacheDirectory.Should().Be("/home/Zoë 李/.cache/aaml");
        paths.RuntimeDirectory.Should().Be("/run/aaml-fallback/aaml");
        paths.UsesRuntimeFallback.Should().BeTrue();
    }
}
