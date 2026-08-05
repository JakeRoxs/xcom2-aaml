using AAML.Infrastructure.Windows.Paths;
using FluentAssertions;

namespace AAML.Infrastructure.Windows.Tests;

[TestClass]
public sealed class WindowsPathSemanticsTests
{
    private readonly WindowsPathSemantics semantics = new();

    [TestMethod]
    public void Normalization_IsHostIndependentCaseInsensitiveAndComponentBased()
    {
        var normalized = semantics.NormalizeIdentity("c:/Games/Mods/../XCOM 2/");

        normalized.IsSuccess.Should().BeTrue();
        normalized.Value.Should().Be("C:\\Games\\XCOM 2");
        semantics.AreEqual("C:\\Games\\XCOM 2", "c:/games/xcom 2/").Should().BeTrue();
    }

    [TestMethod]
    public void ForeignWindowsPath_IsNeverPrefixedWithHostCurrentDirectory()
    {
        var normalized = semantics.NormalizeIdentity("C:\\AML-Fixtures\\Games\\XCOM 2");

        normalized.Value.Should().Be("C:\\AML-Fixtures\\Games\\XCOM 2");
        normalized.Value.Should().NotStartWith(Environment.CurrentDirectory);
    }

    [TestMethod]
    public void Containment_DoesNotConfuseSiblingPrefixes()
    {
        semantics.IsContainedBy("C:\\Mods\\A", "C:\\Mods").Value.Should().BeTrue();
        semantics.IsContainedBy("C:\\Mods2\\A", "C:\\Mods").Value.Should().BeFalse();
    }

    [TestMethod]
    public void RelativeAndRootEscapingPaths_AreRejected()
    {
        semantics.NormalizeIdentity("Mods\\A").Error!.Code.Should().Be("path.not_absolute");
        semantics.NormalizeIdentity("C:\\..\\A").Error!.Code.Should().Be("path.root_escape");
    }
}
