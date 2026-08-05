using System.Xml.Linq;
using FluentAssertions;

namespace AAML.Infrastructure.Common.CharacterizationTests.Resources;

[TestClass]
public sealed class LegacyResourceContractTests
{
    [TestMethod]
    public void MainFormServiceColors_RemainsIsolatedBinaryFormatterPayload()
    {
        var document = XDocument.Parse(CompatibilityFixture.Read("resources", "MainForm.ServiceColors.resx"));

        var resource = document.Descendants("data").Single(element => (string?)element.Attribute("name") == "modinfo_ConfigFCTB.ServiceColors");

        ((string?)resource.Attribute("mimetype")).Should().Be("application/x-microsoft.net.object.binary.base64");
        resource.Element("value")!.Value.Should().NotBeNullOrWhiteSpace();
    }
}
