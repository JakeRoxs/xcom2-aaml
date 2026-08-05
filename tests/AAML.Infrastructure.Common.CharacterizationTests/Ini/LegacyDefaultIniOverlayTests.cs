using AAML.Infrastructure.Common.Compatibility.Ini;
using FluentAssertions;

namespace AAML.Infrastructure.Common.CharacterizationTests.Ini;

[TestClass]
public sealed class LegacyDefaultIniOverlayTests
{
    [TestMethod]
    public void Operators_AppendDuplicateRemoveExactAndClear()
    {
        var document = LegacyIniDocument.Parse("[Section]\nKey=A\nKey=B\nClear=Old\n");
        var overlay = LegacyIniDocument.Parse("[Section]\n+Key=C\n.Key=C\n-Key=A\n!Clear=Ignored\n");

        document.ApplyOverlay(overlay);

        document.Get("Section", "Key").Should().Equal("B", "C", "C");
        document.Get("Section", "Clear").Should().BeEmpty();
    }

    [TestMethod]
    public void PlainRepeatedKey_LastValueWins()
    {
        var document = LegacyIniDocument.Parse("[Section]\nKey=base\n");
        var overlay = LegacyIniDocument.Parse("[Section]\nKey=first\nKey=last\n");

        document.ApplyOverlay(overlay);

        document.Get("Section", "Key").Should().Equal("last");
    }
}
