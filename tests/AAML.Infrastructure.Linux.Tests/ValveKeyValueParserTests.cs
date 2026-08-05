using AAML.Infrastructure.Common.Steam;
using FluentAssertions;

namespace AAML.Infrastructure.Linux.Tests;

[TestClass]
public sealed class ValveKeyValueParserTests
{
    [TestMethod]
    public void Parser_HandlesCommentsEscapesAndObjectAndLegacyValues()
    {
        var entries = ValveKeyValueParser.Parse("// comment\n\"root\" {\n\t\"path\" \"/mnt/Games SSD/Steam\\\\Library\" // tail\n\t\"quote\" \"a \\\"quoted\\\" value\"\n}\n\"legacy\" \"value\"\n");

        entries.Should().HaveCount(2);
        entries[0].Children.Single(child => child.Key == "path").Value.Should().Be("/mnt/Games SSD/Steam\\Library");
        entries[0].Children.Single(child => child.Key == "quote").Value.Should().Be("a \"quoted\" value");
        entries[1].Value.Should().Be("value");
    }

    [TestMethod]
    public void Parser_RejectsUnterminatedStringsAndBraces()
    {
        var unterminated = () => ValveKeyValueParser.Parse("\"root\" { \"key\" \"value");
        var unbalanced = () => ValveKeyValueParser.Parse("\"root\" { \"key\" \"value\" ");

        unterminated.Should().Throw<FormatException>();
        unbalanced.Should().Throw<FormatException>();
    }
}
