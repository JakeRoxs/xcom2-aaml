using System.Diagnostics;
using AAML.Application.Ports;
using AAML.Infrastructure.Windows.Processes;
using FluentAssertions;

namespace AAML.Infrastructure.Windows.Tests;

[TestClass]
public sealed class WindowsProcessServicesTests
{
    [TestMethod]
    public async Task ProcessRunner_UsesNoShellAndPreservesStructuredArguments()
    {
        var starter = new RecordingStarter();
        var runner = new WindowsProcessRunner(starter);
        var request = new ProcessLaunchRequest("C:\\Games\\XCom2.exe", ["-Name=Mixed Case", "&;|$() Ω"], "C:\\Games");

        var result = await runner.StartAsync(request, TestContext.CancellationToken);

        result.Value!.ProcessId.Should().Be(123);
        starter.Last!.UseShellExecute.Should().BeFalse();
        starter.Last.ArgumentList.Should().Equal("-Name=Mixed Case", "&;|$() Ω");
        starter.Last.WorkingDirectory.Should().Be("C:\\Games");
    }

    [TestMethod]
    [DataRow("https://example.invalid")]
    [DataRow("steam://run/268500")]
    public async Task ExternalLauncher_AllowsExplicitSchemesThroughShell(string target)
    {
        var starter = new RecordingStarter();
        var launcher = new WindowsExternalLauncher(starter);

        var result = await launcher.OpenUriAsync(new Uri(target), TestContext.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        starter.Last!.UseShellExecute.Should().BeTrue();
        new Uri(starter.Last.FileName).Should().Be(new Uri(target));
        starter.Last.ArgumentList.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ExternalLauncher_RejectsUnapprovedSchemeAndMissingPaths()
    {
        var launcher = new WindowsExternalLauncher(new RecordingStarter());

        var script = await launcher.OpenUriAsync(new Uri("javascript:alert(1)"), TestContext.CancellationToken);
        var file = await launcher.OpenFileAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), TestContext.CancellationToken);
        var directory = await launcher.OpenDirectoryAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), TestContext.CancellationToken);

        script.Error!.Kind.Should().Be(AAML.Application.Common.ErrorKind.Validation);
        file.Error!.Code.Should().Be("shell.file_not_found");
        directory.Error!.Code.Should().Be("shell.directory_not_found");
    }

    public TestContext TestContext { get; set; }

    private sealed class RecordingStarter : IProcessStarter
    {
        public ProcessStartInfo? Last { get; private set; }
        public int? Start(ProcessStartInfo startInfo) { Last = startInfo; return 123; }
    }
}
