using AAML.Infrastructure.Common.Compatibility.Ini;
using FluentAssertions;

namespace AAML.Infrastructure.Common.CharacterizationTests.Ini;

[TestClass]
public sealed class LegacyIniDocumentTests
{
    [TestMethod]
    public void VanillaEngine_PreservesDuplicateModRootsInOrder()
    {
        var ini = CompatibilityFixture.Read("ini", "vanilla-XComEngine.ini");

        var document = LegacyIniDocument.Parse(ini);

        document.Get("Engine.DownloadableContentEnumerator", "ModRootDirs").Should().Equal(
            "..\\..\\..\\XComGame\\Mods",
            "C:\\AML-Fixtures\\Steam\\steamapps\\workshop\\content\\268500\\");
    }

    [TestMethod]
    public void WotcOptions_PreservesQuotedAndUnquotedValues()
    {
        var ini = CompatibilityFixture.Read("ini", "wotc-XComModOptions.ini");

        var document = LegacyIniDocument.Parse(ini);

        document.Get("Engine.XComModOptions", "ActiveMods").Should().Equal("SyntheticModA", "\"SyntheticModB\"");
    }

    [TestMethod]
    public void Parser_ReplacesGameTokenAndSkipsCommentsAndInvalidLines()
    {
        const string ini = "[Section]\n;Ignored=value\nInvalid\nKey=%GAME%Value\n";

        var document = LegacyIniDocument.Parse(ini);

        document.Get("Section", "Key").Should().Equal("XComValue");
        document.Get("Section", ";Ignored").Should().BeEmpty();
    }
}
