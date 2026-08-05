using AAML.Application.Ports;
using FluentAssertions;

namespace AAML.Infrastructure.Common.CharacterizationTests.Processes;

[TestClass]
public sealed class ProcessLaunchRequestTests
{
    [TestMethod]
    public void Request_PreservesStructuredArgumentsWithoutLaunching()
    {
        var request = new ProcessLaunchRequest(
            "C:\\AML-Fixtures\\Games\\XCOM 2\\Binaries\\Win64\\XCom2.exe",
            ["-review", "-name=Unicode Ω", "-path=C:\\Folder With Spaces"],
            "C:\\AML-Fixtures\\Games\\XCOM 2");

        request.Arguments.Should().Equal("-review", "-name=Unicode Ω", "-path=C:\\Folder With Spaces");
        request.ExecutablePath.Should().EndWith("XCom2.exe");
    }

    [TestMethod]
    public void Request_SnapshotsArgumentsAndPreservesShellMetacharactersAsLiteralTokens()
    {
        var source = new List<string> { "value with spaces", "quoted=\"value\"", "&;|$() Ω" };

        var request = new ProcessLaunchRequest("C:\\Games\\XCom2.exe", source);
        source.Clear();

        request.Arguments.Should().Equal("value with spaces", "quoted=\"value\"", "&;|$() Ω");
    }

    [TestMethod]
    public void Request_RejectsShellUriAsExecutable()
    {
        var action = () => new ProcessLaunchRequest("https://example.invalid", []);

        action.Should().Throw<ArgumentException>();
    }
}
