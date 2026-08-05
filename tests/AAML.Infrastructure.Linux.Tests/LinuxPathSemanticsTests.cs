using AAML.Infrastructure.Linux.Paths;
using FluentAssertions;

namespace AAML.Infrastructure.Linux.Tests;

[TestClass]
public sealed class LinuxPathSemanticsTests
{
    private readonly LinuxPathSemantics semantics = new();

    [TestMethod]
    public void Normalize_IsHostIndependentCaseSensitiveAndPreservesUnicodeSpacesAndBackslashes()
    {
        var result = semantics.NormalizeIdentity("//home/Zoë 李/Games SSD/./Steam/../Library/a\\b/");

        result.Value.Should().Be("/home/Zoë 李/Games SSD/Library/a\\b");
        semantics.AreEqual("/Games/XCOM 2", "/games/XCOM 2").Should().BeFalse();
        semantics.AreEqual("/a\\b", "/a/b").Should().BeFalse();
        result.Value.Should().NotContain(Environment.CurrentDirectory);
    }

    [TestMethod]
    public void Containment_IsComponentSafeAndOrdinal()
    {
        semantics.IsContainedBy("/mods/a", "/mods").Value.Should().BeTrue();
        semantics.IsContainedBy("/mods2/a", "/mods").Value.Should().BeFalse();
        semantics.IsContainedBy("/Mods/a", "/mods").Value.Should().BeFalse();
        semantics.IsContainedBy("/mods", "/mods").Value.Should().BeTrue();
    }

    [TestMethod]
    public void RelativeNullAndRootEscape_AreRejected()
    {
        semantics.NormalizeIdentity("relative/path").Error!.Code.Should().Be("path.not_absolute");
        semantics.NormalizeIdentity("/../../etc").Error!.Code.Should().Be("path.root_escape");
        semantics.NormalizeIdentity("/bad\0path").Error!.Code.Should().Be("path.invalid");
        semantics.NormalizeIdentity("/").Value.Should().Be("/");
    }
}
